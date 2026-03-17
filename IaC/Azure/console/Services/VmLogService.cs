using Azure.Data.Tables;
using OpenClaw.Console.Models;

namespace OpenClaw.Console.Services;

public class VmLogService
{
    private readonly TableClient _table;

    public VmLogService(TableServiceClient tableServiceClient, IConfiguration config)
    {
        var tableName = config["Storage:LogTableName"] ?? "vmlogs";
        _table = tableServiceClient.GetTableClient(tableName);
        _table.CreateIfNotExists();
    }

    /// <summary>
    /// Append a log entry for a VM action. RowKey uses ticks for chronological ordering.
    /// </summary>
    public async Task AppendAsync(string vmName, string action, string text, bool isFinal = false, int? exitCode = null)
    {
        var record = new VmLogRecord
        {
            PartitionKey = vmName,
            RowKey = $"{action}_{DateTime.UtcNow.Ticks:D20}",
            Action = action,
            Text = text.Length > 30000 ? text[^30000..] : text,
            IsFinal = isFinal,
            ExitCode = exitCode,
        };
        await _table.AddEntityAsync(record);
    }

    /// <summary>
    /// Get all log entries for a VM, optionally filtered by action, ordered chronologically.
    /// </summary>
    public async Task<List<VmLogRecord>> GetLogsAsync(string vmName, string? action = null)
    {
        var filter = $"PartitionKey eq '{vmName}'";
        if (!string.IsNullOrEmpty(action))
            filter += $" and Action eq '{action}'";

        var logs = new List<VmLogRecord>();
        await foreach (var entity in _table.QueryAsync<VmLogRecord>(filter: filter))
        {
            logs.Add(entity);
        }
        return logs.OrderBy(l => l.RowKey).ToList();
    }

    /// <summary>
    /// Get the latest action's logs for a VM (e.g. the most recent create or delete).
    /// </summary>
    public async Task<List<VmLogRecord>> GetLatestActionLogsAsync(string vmName)
    {
        var allLogs = await GetLogsAsync(vmName);
        if (allLogs.Count == 0) return allLogs;

        // Find the last action by looking at the latest entry
        var latestAction = allLogs[^1].Action;

        // Get all entries with the same action prefix in RowKey
        return allLogs.Where(l => l.Action == latestAction).ToList();
    }
}
