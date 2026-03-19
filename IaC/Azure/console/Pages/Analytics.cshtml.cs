using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenClaw.Console.Services;

namespace OpenClaw.Console.Pages;

public class AnalyticsModel : PageModel
{
    private readonly GatewayUsageService _usage;

    public AnalyticsModel(GatewayUsageService usage)
    {
        _usage = usage;
    }

    // KPI
    public int TotalMessages { get; set; }
    public int TotalToolCalls { get; set; }
    public double TotalCostUsd { get; set; }
    public int SessionCount { get; set; }
    public long TotalTokens { get; set; }
    public double AvgLatencyMs { get; set; }
    public double GlobalErrorRate { get; set; }

    // Daily trend
    public List<DailyStat> DailyStats { get; set; } = new();

    // Model distribution
    public List<ModelStat> ModelStats { get; set; } = new();

    // Per-VM performance
    public List<VmPerformance> VmPerformances { get; set; } = new();

    // VM counts
    public int ActiveVmCount { get; set; }
    public int ReadyVmCount { get; set; }
    public int TotalVmCount { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            var result = await _usage.GetAggregatedUsageAsync(days: 7);

            TotalMessages = result.TotalMessages;
            TotalToolCalls = result.ToolCallsTotal;
            TotalCostUsd = result.TotalCost;
            SessionCount = result.SessionCount;
            TotalTokens = result.TotalTokens;
            AvgLatencyMs = result.AverageLatencyMs;
            GlobalErrorRate = result.GlobalErrorRate;

            DailyStats = result.DailyStats;
            ModelStats = result.ModelStats;
            VmPerformances = result.VmPerformances;

            ActiveVmCount = result.VmCount;
            ReadyVmCount = result.ReadyVmCount;
            TotalVmCount = result.TotalVmCount;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法获取运营数据: {ex.Message}";
        }
    }
}
