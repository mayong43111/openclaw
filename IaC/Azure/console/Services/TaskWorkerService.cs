using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using OpenClaw.Console.Models;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Services;

/// <summary>
/// Background worker that polls the vm-tasks queue and executes
/// Ansible playbooks (deploy-vm.yml / remove-vm.yml) for each task.
/// </summary>
public class TaskWorkerService : BackgroundService
{
    public static string DiagState { get; set; } = "not-started";

    private readonly QueueClient _queue;
    private readonly VmTableService _table;
    private readonly VmLogService _logService;
    private readonly IConfiguration _config;
    private readonly ILogger<TaskWorkerService> _logger;
    private readonly string _ansibleDir;

    public TaskWorkerService(
        QueueServiceClient queueServiceClient,
        VmTableService table,
        VmLogService logService,
        IConfiguration config,
        ILogger<TaskWorkerService> logger)
    {
        var queueName = config["Storage:QueueName"] ?? "vm-tasks";
        _queue = queueServiceClient.GetQueueClient(queueName);
        _table = table;
        _logService = logService;
        _config = config;
        _logger = logger;
        _ansibleDir = config["Worker:AnsibleDir"] ?? "/app/ansible";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiagState = "starting";
        _logger.LogInformation("TaskWorkerService started, polling queue...");

        // Login to Azure CLI using Managed Identity (required for ansible playbooks that call az commands)
        DiagState = "az-login";
        var (loginExit, loginOutput) = await RunCommandAsync("az", "login --identity --allow-no-subscriptions", stoppingToken);
        if (loginExit == 0)
            _logger.LogInformation("Azure CLI MI login succeeded");
        else
            _logger.LogWarning("Azure CLI MI login failed (exit {ExitCode}): {Output} — az commands in playbooks may fail", loginExit, loginOutput);

        DiagState = "polling";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _queue.ReceiveMessageAsync(
                    visibilityTimeout: TimeSpan.FromMinutes(30),
                    cancellationToken: stoppingToken);

                if (response.Value is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var msg = response.Value;
                DiagState = $"processing:{msg.MessageId}";
                await ProcessMessageAsync(msg, stoppingToken);
                await _queue.DeleteMessageAsync(msg.MessageId, msg.PopReceipt, stoppingToken);
                DiagState = "polling";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagState = $"error:{ex.GetType().Name}";
                _logger.LogError(ex, "Error processing queue message");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                DiagState = "polling";
            }
        }
    }

    private async Task ProcessMessageAsync(QueueMessage msg, CancellationToken ct)
    {
        VmTaskMessage? task;
        try
        {
            task = JsonSerializer.Deserialize(msg.Body.ToString(), JsonContext.Default.VmTaskMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message: {Body}", msg.Body);
            return;
        }

        if (task is null || string.IsNullOrEmpty(task.VmName))
        {
            _logger.LogWarning("Ignoring invalid task message");
            return;
        }

        _logger.LogInformation("Processing {Action} for {VmName}", task.Action, task.VmName);

        try
        {
            switch (task.Action)
            {
                case "create":
                    await HandleCreateAsync(task, ct);
                    break;
                case "delete":
                    await HandleDeleteAsync(task, ct);
                    break;
                case "restart":
                    await HandleRestartAsync(task, ct);
                    break;
                default:
                    _logger.LogWarning("Unknown action: {Action}", task.Action);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in {Action} for {VmName}", task.Action, task.VmName);
            await SetFailedAsync(task.VmName, ex.Message);
        }
    }

    private async Task HandleCreateAsync(VmTaskMessage task, CancellationToken ct)
    {
        var aoaiApiKey = _config["AOAI:ApiKey"] ?? "";
        var imageName = task.ImageName ?? _config["Vm:DefaultImageName"] ?? "openclaw-windows-2026.3.12";
        var extraVarsJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["vm_name"] = task.VmName,
            ["vm_admin_password"] = task.VmAdminPassword ?? "",
            ["aoai_api_key"] = aoaiApiKey,
            ["image_name"] = imageName,
        });

        await _logService.AppendAsync(task.VmName, "create", $"[{DateTime.UtcNow:HH:mm:ss}] Starting deploy-vm.yml for {task.VmName} (image: {imageName})");

        var (exitCode, output) = await RunAnsiblePlaybookAsync(
            "deploy-vm.yml", extraVarsJson, task.VmName, "create", ct);

        if (exitCode == 0)
        {
            _logger.LogInformation("deploy-vm.yml succeeded for {VmName}", task.VmName);
            await UpdateTableFromVmsRegistryAsync(task.VmName);
            await _logService.AppendAsync(task.VmName, "create", $"[{DateTime.UtcNow:HH:mm:ss}] ✅ deploy-vm.yml succeeded", isFinal: true, exitCode: 0);
        }
        else
        {
            // Check if Ansible output contains connection info (partial success — VM was created but a later step failed)
            if (output.Contains("Token:") && output.Contains("IP:"))
            {
                _logger.LogWarning("deploy-vm.yml exited {ExitCode} but connection info present — treating as success for {VmName}", exitCode, task.VmName);
                await UpdateTableFromVmsRegistryAsync(task.VmName);
                await _logService.AppendAsync(task.VmName, "create", $"[{DateTime.UtcNow:HH:mm:ss}] ⚠️ deploy-vm.yml exited {exitCode} but VM info found — treating as success", isFinal: true, exitCode: exitCode);
            }
            else
            {
                _logger.LogError("deploy-vm.yml failed for {VmName}: {Output}", task.VmName, output);
                await SetFailedAsync(task.VmName, output);
                await _logService.AppendAsync(task.VmName, "create", $"[{DateTime.UtcNow:HH:mm:ss}] ❌ deploy-vm.yml failed (exit code {exitCode})", isFinal: true, exitCode: exitCode);
            }
        }
    }

    private async Task HandleDeleteAsync(VmTaskMessage task, CancellationToken ct)
    {
        var extraVarsJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["vm_name"] = task.VmName,
        });

        await _logService.AppendAsync(task.VmName, "delete", $"[{DateTime.UtcNow:HH:mm:ss}] Starting remove-vm.yml for {task.VmName}");

        var (exitCode, output) = await RunAnsiblePlaybookAsync(
            "remove-vm.yml", extraVarsJson, task.VmName, "delete", ct);

        if (exitCode == 0)
        {
            _logger.LogInformation("remove-vm.yml succeeded for {VmName}", task.VmName);
            await _logService.AppendAsync(task.VmName, "delete", $"[{DateTime.UtcNow:HH:mm:ss}] ✅ remove-vm.yml succeeded", isFinal: true, exitCode: 0);
            await _table.DeleteAsync(task.VmName);
        }
        else
        {
            _logger.LogError("remove-vm.yml failed for {VmName}: {Output}", task.VmName, output);
            await SetFailedAsync(task.VmName, output);
            await _logService.AppendAsync(task.VmName, "delete", $"[{DateTime.UtcNow:HH:mm:ss}] ❌ remove-vm.yml failed (exit code {exitCode})", isFinal: true, exitCode: exitCode);
        }
    }

    private async Task HandleRestartAsync(VmTaskMessage task, CancellationToken ct)
    {
        // Restart = SSH into VM and restart the scheduled task
        var vm = await _table.GetAsync(task.VmName);
        if (vm is null) return;

        var rg = _config["Azure:ResourceGroup"] ?? "rg-ymms-openclaw-infra";
        var script = "Get-ScheduledTask -TaskName 'OpenClawGateway' | Stop-ScheduledTask; Start-ScheduledTask -TaskName 'OpenClawGateway'";

        await _logService.AppendAsync(task.VmName, "restart", $"[{DateTime.UtcNow:HH:mm:ss}] Starting restart for {task.VmName}");

        var (exitCode, output) = await RunCommandAsync(
            "az", $"vm run-command invoke --resource-group {rg} --name {task.VmName} --command-id RunPowerShellScript --scripts \"{script}\" -o json",
            ct);

        if (exitCode == 0)
        {
            vm.Status = "ready";
            await _table.UpsertAsync(vm);
            _logger.LogInformation("Restart succeeded for {VmName}", task.VmName);
            await _logService.AppendAsync(task.VmName, "restart", $"[{DateTime.UtcNow:HH:mm:ss}] ✅ Restart succeeded", isFinal: true, exitCode: 0);
        }
        else
        {
            await SetFailedAsync(task.VmName, $"Restart failed: {output}");
            await _logService.AppendAsync(task.VmName, "restart", $"[{DateTime.UtcNow:HH:mm:ss}] ❌ Restart failed (exit code {exitCode})\n{output}", isFinal: true, exitCode: exitCode);
        }
    }

    private async Task<(int exitCode, string output)> RunAnsiblePlaybookAsync(
        string playbook, string extraVarsJson, string vmName, string action, CancellationToken ct)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, extraVarsJson, ct);
            var args = $"-i {_ansibleDir}/inventory/hosts.yml {_ansibleDir}/{playbook} -e @{tempFile}";
            return await RunCommandWithLoggingAsync("ansible-playbook", args, vmName, action, ct);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Run a command and stream stdout/stderr to Table Storage logs in batches.
    /// </summary>
    private async Task<(int exitCode, string output)> RunCommandWithLoggingAsync(
        string command, string args, string vmName, string action, CancellationToken ct)
    {
        _logger.LogInformation("Running: {Command} {Args}", command, args);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _ansibleDir,
        };
        psi.Environment["AZURE_DEFAULTS_GROUP"] = _config["Azure:ResourceGroup"] ?? "";
        // Force Ansible to not buffer output and show task results immediately
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["ANSIBLE_FORCE_COLOR"] = "false";

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var fullOutput = new System.Text.StringBuilder();
        var batch = new System.Text.StringBuilder();
        var lastFlush = DateTime.UtcNow;
        var batchLock = new SemaphoreSlim(1, 1);
        const int FlushIntervalSeconds = 5;
        const int MaxBatchChars = 4000;

        // Read stdout and stderr concurrently, streaming to log table
        async Task ReadStreamAsync(System.IO.StreamReader reader, string prefix)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                var formatted = string.IsNullOrEmpty(prefix) ? line : $"[{prefix}] {line}";

                await batchLock.WaitAsync(ct);
                try
                {
                    fullOutput.AppendLine(formatted);
                    batch.AppendLine(formatted);

                    var elapsed = (DateTime.UtcNow - lastFlush).TotalSeconds;
                    if (elapsed >= FlushIntervalSeconds || batch.Length >= MaxBatchChars)
                    {
                        await FlushBatchAsync(vmName, action, batch);
                        lastFlush = DateTime.UtcNow;
                    }
                }
                finally
                {
                    batchLock.Release();
                }
            }
        }

        var stdoutTask = ReadStreamAsync(proc.StandardOutput, "");
        var stderrTask = ReadStreamAsync(proc.StandardError, "STDERR");
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        // Flush remaining
        if (batch.Length > 0)
            await FlushBatchAsync(vmName, action, batch);

        var combined = fullOutput.ToString();
        if (combined.Length > 2000)
            combined = combined[^2000..];

        return (proc.ExitCode, combined);
    }

    private async Task FlushBatchAsync(string vmName, string action, System.Text.StringBuilder batch)
    {
        var text = batch.ToString();
        batch.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            await _logService.AppendAsync(vmName, action, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write log batch for {VmName}", vmName);
        }
    }

    private async Task<(int exitCode, string output)> RunCommandAsync(
        string command, string args, CancellationToken ct)
    {
        _logger.LogInformation("Running: {Command} {Args}", command, args);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _ansibleDir,
        };
        // Inherit MI credentials for az cli
        psi.Environment["AZURE_DEFAULTS_GROUP"] = _config["Azure:ResourceGroup"] ?? "";

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n--- STDERR ---\n{stderr}";

        // Keep last 2000 chars to avoid Table Storage column size limits
        if (combined.Length > 2000)
            combined = combined[^2000..];

        return (proc.ExitCode, combined);
    }

    /// <summary>
    /// Read VM connection info from vms.yml written by deploy-vm.yml Play 3.
    /// Within a single deploy session the file is freshly written and reliable.
    /// Format (YAML):
    ///   vms:
    ///     - name: "vm-ymms-openclaw-13"
    ///       ip: "10.0.1.4"
    ///       port: 18789
    ///       token: "abc123..."
    ///       url: "https://vm-ymms-openclaw-13.20-38-7-158.nip.io"
    ///       ...
    /// </summary>
    private async Task UpdateTableFromVmsRegistryAsync(string vmName)
    {
        var vmsPath = Path.Combine(_ansibleDir, "vms.yml");
        if (!File.Exists(vmsPath))
        {
            _logger.LogWarning("vms.yml not found at {Path} — cannot update VM record for {VmName}", vmsPath, vmName);
            // Still mark as ready even without details
            var fallback = await _table.GetAsync(vmName) ?? new VmRecord { RowKey = vmName };
            fallback.Status = "ready";
            await _table.UpsertAsync(fallback);
            return;
        }

        var content = await File.ReadAllTextAsync(vmsPath);
        var vm = await _table.GetAsync(vmName) ?? new VmRecord { RowKey = vmName };
        vm.Status = "ready";
        vm.CompletedAt = DateTimeOffset.UtcNow;

        // Simple line-based YAML parsing for the target VM's block
        var lines = content.Split('\n');
        bool inTargetBlock = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            // Detect start of a VM entry: "- name: "vm-ymms-openclaw-13""
            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                var nameVal = line["- name:".Length..].Trim().Trim('"');
                inTargetBlock = string.Equals(nameVal, vmName, StringComparison.Ordinal);
                continue;
            }
            if (!inTargetBlock) continue;
            // Next list item starts → stop
            if (line.StartsWith("- ", StringComparison.Ordinal)) break;

            if (line.StartsWith("ip:", StringComparison.Ordinal))
                vm.VmIp = line["ip:".Length..].Trim().Trim('"');
            else if (line.StartsWith("token:", StringComparison.Ordinal))
                vm.Token = line["token:".Length..].Trim().Trim('"');
            else if (line.StartsWith("url:", StringComparison.Ordinal))
                vm.Url = line["url:".Length..].Trim().Trim('"');
        }

        if (string.IsNullOrEmpty(vm.VmIp))
            _logger.LogWarning("Could not find IP in vms.yml for {VmName}", vmName);

        await _table.UpsertAsync(vm);
    }

    private async Task SetFailedAsync(string vmName, string error)
    {
        var vm = await _table.GetAsync(vmName);
        if (vm is null) return;
        vm.Status = "failed";
        vm.Error = error;
        await _table.UpsertAsync(vm);
    }
}
