using Azure;
using Azure.Data.Tables;

namespace OpenClaw.Console.Models;

public class ImageRecord : ITableEntity
{
    public string PartitionKey { get; set; } = "image";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Azure image resource ID.</summary>
    public string ImageId { get; set; } = string.Empty;

    /// <summary>Human-readable display name (e.g. "OpenClaw 2026.3.12").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>OS type (Windows / Linux).</summary>
    public string OsType { get; set; } = "Windows";

    /// <summary>Whether this image is available for new VM creation.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Image name derived from RowKey.</summary>
    public string Name => RowKey;
}
