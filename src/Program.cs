using IISDefensiveAI.Agent;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5005");

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "IISDefensiveAI.Agent";
    });
}

builder.Services.Configure<LogMonitoringOptions>(builder.Configuration.GetSection(LogMonitoringOptions.SectionName));
builder.Services.Configure<LogAnalyticsOptions>(builder.Configuration.GetSection(LogAnalyticsOptions.SectionName));
builder.Services.Configure<DiagnosticReasoningOptions>(builder.Configuration.GetSection(DiagnosticReasoningOptions.SectionName));
builder.Services.AddHttpClient<DiagnosticReasoningService>((sp, client) =>
{
    var o = sp.GetRequiredService<IOptions<DiagnosticReasoningOptions>>().Value;
    var baseUrl = o.OllamaBaseUrl.TrimEnd('/');
    client.BaseAddress = new Uri(baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, o.RequestTimeoutSeconds));
});
builder.Services.AddSingleton<IISController>();
builder.Services.AddSingleton<PostActionAuditService>();
builder.Services.AddSingleton<AnomalyTelemetry>();
builder.Services.AddSingleton<LogAnalyticsService>();
// Same LogMonitoringService instance for hosted execution and consumers (e.g. LogAnalyticsService).
builder.Services.AddSingleton<LogMonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogMonitoringService>());
builder.Services.AddSingleton<NightlyLearningService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NightlyLearningService>());

var app = builder.Build();

app.MapGet("/status", (IISController iis, IOptions<LogMonitoringOptions> opts, AnomalyTelemetry telemetry) =>
{
    var authorized = (opts.Value.AuthorizedAppPools ?? new List<string>())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.Trim())
        .ToList();

    if (authorized.Count == 0)
    {
        var allPools = iis.GetAppPoolStatuses();
        return Results.Json(new AgentStatusResponse
        {
            AppPoolName = null,
            AppPoolStatus = "no_authorized_pools",
            AppPools = allPools,
            RecentAnomalies = telemetry.GetRecent(),
        });
    }

    var appPools = authorized
        .Select(name => new AppPoolStatus(name, iis.GetAppPoolStatus(name)))
        .ToList();

    return Results.Json(new AgentStatusResponse
    {
        AppPoolName = authorized.Count == 1 ? authorized[0] : null,
        AppPoolStatus = "authorized",
        AppPools = appPools,
        RecentAnomalies = telemetry.GetRecent(),
    });
});

app.MapGet("/analytics", async (LogAnalyticsService analytics, CancellationToken cancellationToken) =>
{
    var report = await analytics.GetStatsAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(report);
});

app.MapGet("/baseline", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "baseline_profile.json");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "baseline_profile.json not found", path });

    var text = File.ReadAllText(path);
    return Results.Text(text, "application/json; charset=utf-8");
});

app.MapPost("/mark-safe", (MarkSafeRequest body, IISController iis) =>
{
    if (string.IsNullOrWhiteSpace(body.RequestPath))
        return Results.BadRequest(new { error = "RequestPath is required." });

    var path = body.RequestPath.Trim();
    iis.MarkAsSafe(path, body.LatencyMs);
    return Results.Ok(new { ok = true, requestPath = path, latencyMs = body.LatencyMs });
});

app.MapPost(
    "/analyze-logs",
    (DiagnosticReasoningService reasoning, IOptions<LogMonitoringOptions> opts, IHostEnvironment env) =>
    {
        var o = opts.Value;
        var rawDir = o.LogDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(rawDir))
            return Results.BadRequest(new { success = false, error = "LogMonitoring:LogDirectory is not configured." });

        var directory = Path.IsPathRooted(rawDir)
            ? Path.GetFullPath(rawDir)
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, rawDir));

        if (!Directory.Exists(directory))
            return Results.NotFound(new { success = false, error = "Log directory does not exist.", path = directory });

        var filter = string.IsNullOrWhiteSpace(o.FileFilter) ? "*.json" : o.FileFilter.Trim();
        var contentRoot = env.ContentRootPath;

        _ = Task.Run(() => reasoning.AnalyzeLogFolderAsync(directory, filter, contentRoot, CancellationToken.None));

        return Results.Json(new { success = true, message = "Batch log analysis has started." });
    });

app.MapPost("/rebaseline", (NightlyLearningService nightly) =>
{
    _ = Task.Run(() => nightly.RunLearningJobAsync(CancellationToken.None));
    return Results.Ok(new { message = "Baseline regeneration has started." });
});

app.Run();
