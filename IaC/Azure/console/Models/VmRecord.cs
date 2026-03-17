using Azure;
using Azure.Data.Tables;

namespace OpenClaw.Console.Models;

public class VmRecord : ITableEntity
{
    public string PartitionKey { get; set; } = "vm";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Status { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? VmIp { get; set; }
    public string? Token { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? Tag { get; set; }

    /// <summary>Display name derived from RowKey.</summary>
    public string Name => RowKey;

    public string StatusBadgeClass => Status switch
    {
        "ready" => "bg-success",
        "creating" => "bg-primary",
        "deleting" => "bg-warning text-dark",
        "restarting" => "bg-info text-dark",
        "failed" => "bg-danger",
        _ => "bg-secondary",
    };
}
