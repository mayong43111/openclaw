using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Console.Models;
using System.Globalization;

namespace OpenClaw.Console.Services;

/// <summary>
/// Queries OpenClaw Gateway instances via WebSocket to collect tool/session usage data.
/// Connects to each running VM dynamically (from Table Storage), no hardcoded IPs.
/// </summary>
public class GatewayUsageService
{
    private readonly VmTableService _vmTable;
    private readonly IConfiguration _config;
    private readonly ILogger<GatewayUsageService> _logger;

    public GatewayUsageService(
        VmTableService vmTable,
        IConfiguration config,
        ILogger<GatewayUsageService> logger)
    {
        _vmTable = vmTable;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Query sessions.usage from all running VMs and merge results.
    /// </summary>
    public async Task<AggregatedUsage> GetAggregatedUsageAsync(int days = 1, CancellationToken ct = default)
    {
        var vms = await _vmTable.GetAllAsync();
        var readyVms = vms.Where(v => v.Status == "ready" && !string.IsNullOrEmpty(v.VmIp)).ToList();

        var startDate = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var tasks = readyVms.Select(vm => QueryVmUsageAsync(vm, startDate, endDate, ct));
        var results = await Task.WhenAll(tasks);

        return MergeUsageResults(results.Where(r => r is not null).Cast<VmUsageResult>().ToList());
    }

    private async Task<VmUsageResult?> QueryVmUsageAsync(VmRecord vm, string startDate, string endDate, CancellationToken ct)
    {
        var port = _config.GetValue("Gateway:Port", 18789);
        var token = _config["Gateway:AuthToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Gateway:AuthToken not configured, skipping VM {VmName}", vm.Name);
            return null;
        }

        var uri = new Uri($"ws://{vm.VmIp}:{port}");
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "openclaw-console/1.0");
        // Control UI origin check requires Origin header matching the gateway host.
        ws.Options.SetRequestHeader("Origin", $"http://{vm.VmIp}:{port}");

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(uri, connectCts.Token);

            // 1. Read challenge event
            var challenge = await ReceiveJsonAsync(ws, ct);

            // 2. Send connect handshake
            var connectReq = new
            {
                type = "req",
                id = "connect-1",
                method = "connect",
                @params = new
                {
                    minProtocol = 3,
                    maxProtocol = 3,
                    client = new { id = "openclaw-control-ui", version = "1.0.0", platform = "linux", mode = "ui" },
                    role = "operator",
                    scopes = new[] { "operator.read" },
                    caps = Array.Empty<string>(),
                    commands = Array.Empty<string>(),
                    permissions = new { },
                    auth = new { token },
                }
            };
            await SendJsonAsync(ws, connectReq, ct);

            // 3. Read connect response
            var connectRes = await ReceiveJsonAsync(ws, ct);
            if (connectRes?.GetProperty("ok").GetBoolean() != true)
            {
                _logger.LogWarning("Gateway connect failed for VM {VmName}: {Res}", vm.Name, connectRes);
                return null;
            }

            // 4. Send sessions.usage request
            var usageReq = new
            {
                type = "req",
                id = "usage-1",
                method = "sessions.usage",
                @params = new { startDate, endDate, limit = 200 }
            };
            await SendJsonAsync(ws, usageReq, ct);

            // 5. Read usage response (skip any interim events)
            JsonElement? usageRes = null;
            for (var i = 0; i < 20; i++)
            {
                var msg = await ReceiveJsonAsync(ws, ct);
                if (msg is null) break;
                var msgType = msg.Value.GetProperty("type").GetString();
                if (msgType == "res" && msg.Value.GetProperty("id").GetString() == "usage-1")
                {
                    usageRes = msg;
                    break;
                }
            }

            // 6. Graceful close
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }

            if (usageRes is null || usageRes.Value.GetProperty("ok").GetBoolean() != true)
            {
                _logger.LogWarning("sessions.usage failed or empty for VM {VmName}", vm.Name);
                return null;
            }

            var payload = usageRes.Value.GetProperty("payload");
            return new VmUsageResult { VmName = vm.Name, Payload = payload };
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to query Gateway on VM {VmName} ({VmIp})", vm.Name, vm.VmIp);
            return null;
        }
    }

    private AggregatedUsage MergeUsageResults(List<VmUsageResult> results)
    {
        var merged = new AggregatedUsage();

        foreach (var result in results)
        {
            try
            {
                var payload = result.Payload;
                if (!payload.TryGetProperty("aggregates", out var agg))
                    continue;

                // Merge tool usage
                if (agg.TryGetProperty("tools", out var tools) && tools.TryGetProperty("tools", out var toolList))
                {
                    foreach (var tool in toolList.EnumerateArray())
                    {
                        var name = tool.GetProperty("name").GetString() ?? "unknown";
                        var count = tool.GetProperty("count").GetInt32();
                        if (merged.ToolCalls.ContainsKey(name))
                            merged.ToolCalls[name] += count;
                        else
                            merged.ToolCalls[name] = count;
                    }
                }

                // Merge message counts
                if (agg.TryGetProperty("messages", out var msgs))
                {
                    merged.TotalMessages += msgs.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    merged.ToolCallsTotal += msgs.TryGetProperty("toolCalls", out var tc) ? tc.GetInt32() : 0;
                    merged.Errors += msgs.TryGetProperty("errors", out var e) ? e.GetInt32() : 0;
                }

                // Merge latency
                if (agg.TryGetProperty("latency", out var lat))
                {
                    var avgMs = lat.TryGetProperty("avgMs", out var a) ? a.GetDouble() : 0;
                    var count = lat.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    merged.LatencySamples.Add(new LatencySample { AvgMs = avgMs, Count = count });
                }

                merged.VmCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error merging usage from VM {VmName}", result.VmName);
            }
        }

        return merged;
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeoutCts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Position = 0;
        var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private class VmUsageResult
    {
        public string VmName { get; set; } = "";
        public JsonElement Payload { get; set; }
    }
}

/// <summary>Aggregated usage data from all Gateway instances.</summary>
public class AggregatedUsage
{
    /// <summary>Tool name → total call count across all VMs.</summary>
    public Dictionary<string, int> ToolCalls { get; set; } = new();
    public int TotalMessages { get; set; }
    public int ToolCallsTotal { get; set; }
    public int Errors { get; set; }
    public int VmCount { get; set; }
    public List<LatencySample> LatencySamples { get; set; } = new();

    /// <summary>Weighted average latency across all VMs.</summary>
    public double AverageLatencyMs
    {
        get
        {
            var totalWeight = LatencySamples.Sum(s => s.Count);
            if (totalWeight == 0) return 0;
            return LatencySamples.Sum(s => s.AvgMs * s.Count) / totalWeight;
        }
    }

    /// <summary>Per-tool usage stats for display.</summary>
    public List<ToolUsageStat> GetToolStats()
    {
        var totalErrors = Errors;
        var totalCalls = ToolCallsTotal > 0 ? ToolCallsTotal : ToolCalls.Values.Sum();

        return ToolCalls
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ToolUsageStat
            {
                Name = kv.Key,
                Calls = kv.Value,
                AvgLatencyMs = AverageLatencyMs,
                // Error rate is global (Gateway doesn't break down errors per tool)
                ErrorRate = totalCalls > 0 ? (double)totalErrors / totalCalls * 100 : 0,
            })
            .ToList();
    }
}

public class LatencySample
{
    public double AvgMs { get; set; }
    public int Count { get; set; }
}

public class ToolUsageStat
{
    public string Name { get; set; } = "";
    public int Calls { get; set; }
    public double AvgLatencyMs { get; set; }
    public double ErrorRate { get; set; }

    public string FormattedLatency => AvgLatencyMs switch
    {
        >= 1000 => $"{AvgLatencyMs / 1000:F1}s",
        > 0 => $"{AvgLatencyMs:F0}ms",
        _ => "—",
    };

    public string ErrorRateCssClass => ErrorRate switch
    {
        > 5 => "text-danger",
        > 2 => "text-warning",
        _ => "text-success",
    };
}
