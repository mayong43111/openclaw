using Azure;
using Azure.Data.Tables;

namespace OpenClaw.Console.Models;

public class VmLogRecord : ITableEntity
{
    /// <summary>PartitionKey = VM name (e.g. "vm-ymms-openclaw-07").</summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>RowKey = "{action}_{timestamp ticks}" for chronological ordering.</summary>
    public string RowKey { get; set; } = string.Empty;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Action that produced this log (create, delete, restart).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Log text content (one or more lines).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Whether this entry marks the final result (success/failure).</summary>
    public bool IsFinal { get; set; }

    /// <summary>Exit code of the playbook, set only on IsFinal entries.</summary>
    public int? ExitCode { get; set; }
}
