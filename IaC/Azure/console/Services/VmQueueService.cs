using Azure.Storage.Queues;
using OpenClaw.Console.Models;

namespace OpenClaw.Console.Services;

public class VmQueueService
{
    private readonly QueueClient _queue;

    public VmQueueService(QueueServiceClient queueServiceClient, IConfiguration config)
    {
        var queueName = config["Storage:QueueName"] ?? "vm-tasks";
        _queue = queueServiceClient.GetQueueClient(queueName);
    }

    public async Task SendCreateAsync(string vmName, string adminPassword, string? imageName = null)
    {
        var msg = new VmTaskMessage
        {
            Action = "create",
            VmName = vmName,
            VmAdminPassword = adminPassword,
            ImageName = imageName,
        };
        await _queue.SendMessageAsync(BinaryData.FromString(msg.ToJson()));
    }

    public async Task SendDeleteAsync(string vmName)
    {
        var msg = new VmTaskMessage { Action = "delete", VmName = vmName };
        await _queue.SendMessageAsync(BinaryData.FromString(msg.ToJson()));
    }

    public async Task SendRestartAsync(string vmName)
    {
        var msg = new VmTaskMessage { Action = "restart", VmName = vmName };
        await _queue.SendMessageAsync(BinaryData.FromString(msg.ToJson()));
    }
}
