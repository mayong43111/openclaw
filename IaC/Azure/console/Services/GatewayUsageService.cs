using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Console.Models;
using System.Globalization;

namespace OpenClaw.Console.Services;

/// <summary>
/// Queries OpenClaw Gateway instances via WebSocket to collect tool/session usage data.
/// Connects to each running VM dynamically (from Table Storage), no hardcoded IPs.
/// </summary>
public class GatewayUsageService
{
    private readonly VmTableService _vmTable;
    private readonly IConfiguration _config;
    private readonly ILogger<GatewayUsageService> _logger;

    public GatewayUsageService(
        VmTableService vmTable,
        IConfiguration config,
        ILogger<GatewayUsageService> logger)
    {
        _vmTable = vmTable;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Query sessions.usage from all running VMs and merge results.
    /// </summary>
    public async Task<AggregatedUsage> GetAggregatedUsageAsync(int days = 1, CancellationToken ct = default)
    {
        var globalToken = _config["Gateway:AuthToken"];
        var vms = await _vmTable.GetAllAsync();
        var readyVms = vms.Where(v => v.Status == "ready" && !string.IsNullOrEmpty(v.VmIp)).ToList();

        var startDate = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var tasks = readyVms.Select(vm => QueryVmUsageAsync(vm, globalToken, startDate, endDate, ct));
        var results = await Task.WhenAll(tasks);

        var merged = MergeUsageResults(results.Where(r => r is not null).Cast<VmUsageResult>().ToList());
        merged.ReadyVmCount = readyVms.Count;
        merged.TotalVmCount = vms.Count;
        return merged;
    }

    private async Task<VmUsageResult?> QueryVmUsageAsync(VmRecord vm, string? globalToken, string startDate, string endDate, CancellationToken ct)
    {
        var port = _config.GetValue("Gateway:Port", 18789);
        // Prefer per-VM token from Table Storage, fall back to global config.
        var token = !string.IsNullOrEmpty(vm.Token) ? vm.Token : globalToken;
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("No auth token for VM {VmName} (neither per-VM nor global), skipping", vm.Name);
            return null;
        }

        var uri = new Uri($"ws://{vm.VmIp}:{port}");
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "openclaw-console/1.0");
        // Control UI origin check requires Origin header matching the gateway host.
        ws.Options.SetRequestHeader("Origin", $"http://{vm.VmIp}:{port}");

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(uri, connectCts.Token);

            // 1. Read challenge event
            var challenge = await ReceiveJsonAsync(ws, ct);

            // 2. Send connect handshake
            var connectReq = new
            {
                type = "req",
                id = "connect-1",
                method = "connect",
                @params = new
                {
                    minProtocol = 3,
                    maxProtocol = 3,
                    client = new { id = "openclaw-control-ui", version = "1.0.0", platform = "linux", mode = "ui" },
                    role = "operator",
                    scopes = new[] { "operator.read" },
                    caps = Array.Empty<string>(),
                    commands = Array.Empty<string>(),
                    permissions = new { },
                    auth = new { token },
                }
            };
            await SendJsonAsync(ws, connectReq, ct);

            // 3. Read connect response
            var connectRes = await ReceiveJsonAsync(ws, ct);
            if (connectRes?.GetProperty("ok").GetBoolean() != true)
            {
                _logger.LogWarning("Gateway connect failed for VM {VmName}: {Res}", vm.Name, connectRes);
                return null;
            }

            // 4. Send sessions.usage request
            var usageReq = new
            {
                type = "req",
                id = "usage-1",
                method = "sessions.usage",
                @params = new { startDate, endDate, limit = 200 }
            };
            await SendJsonAsync(ws, usageReq, ct);

            // 5. Read usage response (skip any interim events)
            JsonElement? usageRes = null;
            for (var i = 0; i < 20; i++)
            {
                var msg = await ReceiveJsonAsync(ws, ct);
                if (msg is null) break;
                var msgType = msg.Value.GetProperty("type").GetString();
                if (msgType == "res" && msg.Value.GetProperty("id").GetString() == "usage-1")
                {
                    usageRes = msg;
                    break;
                }
            }

            // 6. Graceful close
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }

            if (usageRes is null || usageRes.Value.GetProperty("ok").GetBoolean() != true)
            {
                _logger.LogWarning("sessions.usage failed or empty for VM {VmName}", vm.Name);
                return null;
            }

            var payload = usageRes.Value.GetProperty("payload");
            return new VmUsageResult { VmName = vm.Name, Payload = payload };
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to query Gateway on VM {VmName} ({VmIp})", vm.Name, vm.VmIp);
            return null;
        }
    }

    private AggregatedUsage MergeUsageResults(List<VmUsageResult> results)
    {
        var merged = new AggregatedUsage();

        foreach (var result in results)
        {
            try
            {
                var payload = result.Payload;
                if (!payload.TryGetProperty("aggregates", out var agg))
                    continue;

                // Merge tool usage
                if (agg.TryGetProperty("tools", out var tools) && tools.TryGetProperty("tools", out var toolList))
                {
                    foreach (var tool in toolList.EnumerateArray())
                    {
                        var name = tool.GetProperty("name").GetString() ?? "unknown";
                        var count = tool.GetProperty("count").GetInt32();
                        if (merged.ToolCalls.ContainsKey(name))
                            merged.ToolCalls[name] += count;
                        else
                            merged.ToolCalls[name] = count;
                    }
                }

                // Merge message counts
                if (agg.TryGetProperty("messages", out var msgs))
                {
                    merged.TotalMessages += msgs.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    merged.ToolCallsTotal += msgs.TryGetProperty("toolCalls", out var tc) ? tc.GetInt32() : 0;
                    merged.Errors += msgs.TryGetProperty("errors", out var e) ? e.GetInt32() : 0;
                }

                // Merge latency
                if (agg.TryGetProperty("latency", out var lat))
                {
                    var avgMs = lat.TryGetProperty("avgMs", out var a) ? a.GetDouble() : 0;
                    var count = lat.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    merged.LatencySamples.Add(new LatencySample { AvgMs = avgMs, Count = count });
                }

                // Merge daily breakdown
                if (agg.TryGetProperty("daily", out var daily))
                {
                    foreach (var day in daily.EnumerateArray())
                    {
                        var date = day.GetProperty("date").GetString() ?? "";
                        var dayMessages = day.TryGetProperty("messages", out var dm) ? dm.GetInt32() : 0;
                        var dayToolCalls = day.TryGetProperty("toolCalls", out var dtc) ? dtc.GetInt32() : 0;
                        var dayTokens = day.TryGetProperty("tokens", out var dtkn) ? dtkn.GetInt64() : 0;
                        var dayCost = day.TryGetProperty("cost", out var dc) ? dc.GetDouble() : 0;
                        var dayErrors = day.TryGetProperty("errors", out var de) ? de.GetInt32() : 0;

                        var existing = merged.DailyStats.FirstOrDefault(d => d.Date == date);
                        if (existing != null)
                        {
                            existing.Messages += dayMessages;
                            existing.ToolCalls += dayToolCalls;
                            existing.Tokens += dayTokens;
                            existing.Cost += dayCost;
                            existing.Errors += dayErrors;
                        }
                        else
                        {
                            merged.DailyStats.Add(new DailyStat
                            {
                                Date = date,
                                Messages = dayMessages,
                                ToolCalls = dayToolCalls,
                                Tokens = dayTokens,
                                Cost = dayCost,
                                Errors = dayErrors,
                            });
                        }
                    }
                }

                // Merge cost totals
                if (payload.TryGetProperty("totals", out var totals))
                {
                    merged.TotalCost += totals.TryGetProperty("totalCost", out var costVal) ? costVal.GetDouble() : 0;
                    merged.TotalTokens += totals.TryGetProperty("totalTokens", out var tokVal) ? tokVal.GetInt64() : 0;
                }

                // Merge model usage from aggregates.byModel
                if (agg.TryGetProperty("byModel", out var byModel))
                {
                    foreach (var m in byModel.EnumerateArray())
                    {
                        var model = m.TryGetProperty("model", out var mName) ? mName.GetString() ?? "unknown" : "unknown";
                        var provider = m.TryGetProperty("provider", out var pName) ? pName.GetString() ?? "" : "";
                        var mCount = m.TryGetProperty("count", out var mc) ? mc.GetInt32() : 0;
                        var mCost = 0.0;
                        var mTokens = 0L;
                        if (m.TryGetProperty("totals", out var mt))
                        {
                            mCost = mt.TryGetProperty("totalCost", out var mtc) ? mtc.GetDouble() : 0;
                            mTokens = mt.TryGetProperty("totalTokens", out var mtt) ? mtt.GetInt64() : 0;
                        }

                        var key = $"{provider}/{model}";
                        var existing = merged.ModelStats.FirstOrDefault(x => x.Key == key);
                        if (existing != null)
                        {
                            existing.ApiCalls += mCount;
                            existing.Cost += mCost;
                            existing.Tokens += mTokens;
                        }
                        else
                        {
                            merged.ModelStats.Add(new ModelStat
                            {
                                Key = key,
                                Provider = provider,
                                Model = model,
                                ApiCalls = mCount,
                                Cost = mCost,
                                Tokens = mTokens,
                            });
                        }
                    }
                }

                // Build per-VM performance entry
                var vmPerf = new VmPerformance { VmName = result.VmName };
                if (payload.TryGetProperty("sessions", out var sessions))
                    vmPerf.SessionCount = sessions.GetArrayLength();
                if (agg.TryGetProperty("messages", out var vmMsgs))
                {
                    vmPerf.TotalMessages = vmMsgs.TryGetProperty("total", out var vmt) ? vmt.GetInt32() : 0;
                    vmPerf.ToolCalls = vmMsgs.TryGetProperty("toolCalls", out var vmtc) ? vmtc.GetInt32() : 0;
                    vmPerf.Errors = vmMsgs.TryGetProperty("errors", out var vme) ? vme.GetInt32() : 0;
                }
                if (agg.TryGetProperty("latency", out var vmLat))
                {
                    vmPerf.AvgLatencyMs = vmLat.TryGetProperty("avgMs", out var vla) ? vla.GetDouble() : 0;
                    vmPerf.P95LatencyMs = vmLat.TryGetProperty("p95Ms", out var vlp) ? vlp.GetDouble() : 0;
                }
                if (payload.TryGetProperty("totals", out var vmTotals))
                {
                    vmPerf.TotalCost = vmTotals.TryGetProperty("totalCost", out var vtc) ? vtc.GetDouble() : 0;
                    vmPerf.TotalTokens = vmTotals.TryGetProperty("totalTokens", out var vtt) ? vtt.GetInt64() : 0;
                }
                merged.VmPerformances.Add(vmPerf);

                merged.SessionCount += vmPerf.SessionCount;
                merged.VmCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error merging usage from VM {VmName}", result.VmName);
            }
        }

        // Sort daily stats by date
        merged.DailyStats.Sort((a, b) => string.Compare(a.Date, b.Date, StringComparison.Ordinal));
        // Sort model stats by cost descending
        merged.ModelStats.Sort((a, b) => b.Cost.CompareTo(a.Cost));
        // Sort VM performances by total messages descending
        merged.VmPerformances.Sort((a, b) => b.TotalMessages.CompareTo(a.TotalMessages));

        return merged;
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeoutCts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Position = 0;
        var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private class VmUsageResult
    {
        public string VmName { get; set; } = "";
        public JsonElement Payload { get; set; }
    }
}

/// <summary>Aggregated usage data from all Gateway instances.</summary>
public class AggregatedUsage
{
    /// <summary>Tool name → total call count across all VMs.</summary>
    public Dictionary<string, int> ToolCalls { get; set; } = new();
    public int TotalMessages { get; set; }
    public int ToolCallsTotal { get; set; }
    public int Errors { get; set; }
    /// <summary>VMs that returned data successfully.</summary>
    public int VmCount { get; set; }
    /// <summary>VMs with status=ready in Table Storage.</summary>
    public int ReadyVmCount { get; set; }
    /// <summary>Total VMs in Table Storage (all statuses).</summary>
    public int TotalVmCount { get; set; }
    public List<LatencySample> LatencySamples { get; set; } = new();

    /// <summary>Daily activity breakdown (merged across VMs).</summary>
    public List<DailyStat> DailyStats { get; set; } = new();
    /// <summary>Model usage breakdown.</summary>
    public List<ModelStat> ModelStats { get; set; } = new();
    /// <summary>Per-VM performance breakdown.</summary>
    public List<VmPerformance> VmPerformances { get; set; } = new();
    /// <summary>Total API cost in USD across all VMs.</summary>
    public double TotalCost { get; set; }
    /// <summary>Total tokens consumed across all VMs.</summary>
    public long TotalTokens { get; set; }
    /// <summary>Total sessions across all VMs.</summary>
    public int SessionCount { get; set; }

    /// <summary>Weighted average latency across all VMs.</summary>
    public double AverageLatencyMs
    {
        get
        {
            var totalWeight = LatencySamples.Sum(s => s.Count);
            if (totalWeight == 0) return 0;
            return LatencySamples.Sum(s => s.AvgMs * s.Count) / totalWeight;
        }
    }

    /// <summary>Global error rate percentage.</summary>
    public double GlobalErrorRate
    {
        get
        {
            var totalCalls = ToolCallsTotal > 0 ? ToolCallsTotal : ToolCalls.Values.Sum();
            return totalCalls > 0 ? (double)Errors / totalCalls * 100 : 0;
        }
    }

    /// <summary>Per-tool usage stats for display (calls only — latency/errors are global).</summary>
    public List<ToolUsageStat> GetToolStats()
    {
        return ToolCalls
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ToolUsageStat
            {
                Name = kv.Key,
                Calls = kv.Value,
            })
            .ToList();
    }
}

public class LatencySample
{
    public double AvgMs { get; set; }
    public int Count { get; set; }
}

public class ToolUsageStat
{
    public string Name { get; set; } = "";
    public int Calls { get; set; }
}

public class DailyStat
{
    public string Date { get; set; } = "";
    public int Messages { get; set; }
    public int ToolCalls { get; set; }
    public long Tokens { get; set; }
    public double Cost { get; set; }
    public int Errors { get; set; }

    /// <summary>Short display label (MM-dd or day-of-week).</summary>
    public string Label => DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var dt)
        ? dt.ToString("MM/dd", CultureInfo.InvariantCulture)
        : Date;
}

public class ModelStat
{
    public string Key { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int ApiCalls { get; set; }
    public double Cost { get; set; }
    public long Tokens { get; set; }

    /// <summary>Short display name.</summary>
    public string DisplayName => string.IsNullOrEmpty(Model) ? Key : Model;
}

public class VmPerformance
{
    public string VmName { get; set; } = "";
    public int SessionCount { get; set; }
    public int TotalMessages { get; set; }
    public int ToolCalls { get; set; }
    public int Errors { get; set; }
    public double AvgLatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
    public double TotalCost { get; set; }
    public long TotalTokens { get; set; }

    public double SuccessRate => TotalMessages > 0 ? (double)(TotalMessages - Errors) / TotalMessages * 100 : 100;

    public string FormattedLatency => AvgLatencyMs switch
    {
        >= 1000 => $"{AvgLatencyMs / 1000:F1}s",
        > 0 => $"{AvgLatencyMs:F0}ms",
        _ => "—",
    };
}
