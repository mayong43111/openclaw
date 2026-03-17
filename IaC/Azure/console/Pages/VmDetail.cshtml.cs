using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenClaw.Console.Models;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Pages;

public class VmDetailModel : PageModel
{
    private readonly VmTableService _table;
    private readonly VmLogService _logService;

    public VmDetailModel(VmTableService table, VmLogService logService)
    {
        _table = table;
        _logService = logService;
    }

    public VmRecord? Vm { get; set; }
    public List<VmLogRecord> Logs { get; set; } = new();

    /// <summary>Whether the page should auto-refresh (VM is in a transitional state).</summary>
    public bool AutoRefresh => Vm?.Status is "creating" or "deleting" or "restarting";

    public async Task OnGetAsync(string name)
    {
        Vm = await _table.GetAsync(name);
        if (Vm is not null)
        {
            Logs = await _logService.GetLatestActionLogsAsync(name);
        }
    }
}
