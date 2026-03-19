using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Pages;

public class SkillsModel : PageModel
{
    private readonly GatewayUsageService _usage;

    public SkillsModel(GatewayUsageService usage)
    {
        _usage = usage;
    }

    public List<ToolUsageStat> ToolStats { get; set; } = new();
    public int ActiveVmCount { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            var result = await _usage.GetAggregatedUsageAsync(days: 1);
            ToolStats = result.GetToolStats();
            ActiveVmCount = result.VmCount;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法获取使用统计: {ex.Message}";
        }
    }
}
