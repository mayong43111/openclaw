using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenClaw.Console.Models;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Pages;

public class OperationsModel : PageModel
{
    private readonly VmTableService _table;
    private readonly ImageTableService _imageTable;
    private readonly VmQueueService _queue;
    private readonly IConfiguration _config;

    public OperationsModel(VmTableService table, ImageTableService imageTable, VmQueueService queue, IConfiguration config)
    {
        _table = table;
        _imageTable = imageTable;
        _queue = queue;
        _config = config;
    }

    public List<VmRecord> VmRecords { get; set; } = [];
    public List<ImageRecord> Images { get; set; } = [];
    public int TotalCount => VmRecords.Count;
    public int ReadyCount => VmRecords.Count(v => v.Status == "ready");
    public int CreatingCount => VmRecords.Count(v => v.Status == "creating");
    public int FailedCount => VmRecords.Count(v => v.Status == "failed");
    public List<string> AllTags => VmRecords
        .Where(v => !string.IsNullOrEmpty(v.Tag))
        .Select(v => v.Tag!)
        .Distinct()
        .OrderBy(t => t)
        .ToList();

    public async Task OnGetAsync()
    {
        VmRecords = await _table.GetAllAsync();
        Images = await _imageTable.GetActiveAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string imageName, string? tag)
    {
        var prefix = _config["Vm:Prefix"] ?? "ymms";
        var nextNum = await _table.GetNextVmNumberAsync();

        var vmName = $"vm-{prefix}-openclaw-{nextNum:D2}";
        var password = GeneratePassword(24);

        var record = new VmRecord
        {
            RowKey = vmName,
            Status = "creating",
            CreatedAt = DateTimeOffset.UtcNow,
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
        };
        await _table.UpsertAsync(record);
        await _queue.SendCreateAsync(vmName, password, imageName);

        TempData["Success"] = $"Agent '{vmName}' 创建任务已入队 (镜像: {imageName}, 标签: {record.Tag ?? "无"})。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        var vm = await _table.GetAsync(name);
        if (vm == null)
        {
            TempData["Error"] = $"Agent '{name}' 未找到。";
            return RedirectToPage();
        }

        vm.Status = "deleting";
        await _table.UpsertAsync(vm);
        await _queue.SendDeleteAsync(name);

        TempData["Success"] = $"Agent '{name}' 删除任务已入队。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestartAsync(string name)
    {
        var vm = await _table.GetAsync(name);
        if (vm == null)
        {
            TempData["Error"] = $"Agent '{name}' 未找到。";
            return RedirectToPage();
        }

        vm.Status = "restarting";
        await _table.UpsertAsync(vm);
        await _queue.SendRestartAsync(name);

        TempData["Success"] = $"Agent '{name}' 重启任务已入队。";
        return RedirectToPage();
    }

    private static string GeneratePassword(int length)
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#%&*";
        return RandomNumberGenerator.GetString(chars, length);
    }
}
