using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenClaw.Console.Models;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Pages;

public class DesktopModel : PageModel
{
    private readonly VmTableService _table;

    public DesktopModel(VmTableService table)
    {
        _table = table;
    }

    public List<VmRecord> VmRecords { get; set; } = [];
    public string? SelectedVm { get; set; }

    public async Task OnGetAsync(string? vm)
    {
        VmRecords = await _table.GetAllAsync();
        SelectedVm = vm;
    }
}
