using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.HttpOverrides;
using OpenClaw.Console.Services;

var builder = WebApplication.CreateBuilder(args);

// Azure credential — uses Managed Identity on App Service, az login locally.
var credential = new DefaultAzureCredential();

var storageAccountName = builder.Configuration["Storage:AccountName"] ?? "stymmsopenclaw";
var storageUri = new Uri($"https://{storageAccountName}.table.core.windows.net");
var queueUri = new Uri($"https://{storageAccountName}.queue.core.windows.net");

builder.Services.AddSingleton(new TableServiceClient(storageUri, credential));
builder.Services.AddSingleton(new QueueServiceClient(queueUri, credential));
builder.Services.AddSingleton<VmTableService>();
builder.Services.AddSingleton<ImageTableService>();
builder.Services.AddSingleton<VmLogService>();
builder.Services.AddSingleton<VmQueueService>();
if (!builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<TaskWorkerService>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Trust forwarded headers from nginx / App Service / AppGW
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Trust all proxies in container + App Service + AppGW chain
    KnownNetworks = { new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("0.0.0.0"), 0) },
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

// Unauthenticated health check for diagnostics
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow,
    workerState = TaskWorkerService.DiagState,
}));

// ─── REST API ───────────────────────────────────────────
var api = app.MapGroup("/api/vm");

api.MapGet("/", async (VmTableService table) =>
{
    var vms = await table.GetAllAsync();
    return Results.Ok(vms.Select(v => new
    {
        v.Name,
        v.Status,
        v.VmIp,
        v.Url,
        v.CreatedAt,
        v.Error,
    }));
});

api.MapGet("/{name}", async (string name, VmTableService table) =>
{
    var vm = await table.GetAsync(name);
    return vm is null
        ? Results.NotFound(new { error = $"VM '{name}' not found" })
        : Results.Ok(new { vm.Name, vm.Status, vm.VmIp, vm.Url, vm.Token, vm.CreatedAt, vm.Error });
});

api.MapGet("/{name}/logs", async (string name, VmLogService logService) =>
{
    var logs = await logService.GetLatestActionLogsAsync(name);
    return Results.Ok(logs.Select(l => new { l.Action, l.Text, l.Timestamp, l.IsFinal, l.ExitCode }));
});

app.Run();
