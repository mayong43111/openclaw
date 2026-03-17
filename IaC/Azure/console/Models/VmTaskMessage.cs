using System.Text.Json;

namespace OpenClaw.Console.Models;

public class VmTaskMessage
{
    public string Action { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public string? VmAdminPassword { get; set; }
    public string? ImageName { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonContext.Default.VmTaskMessage);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(VmTaskMessage))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
