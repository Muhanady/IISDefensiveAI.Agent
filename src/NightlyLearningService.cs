using System.Text.Json;
using Cronos;
using IISDefensiveAI.Agent.Models;
using Microsoft.Extensions.Options;

namespace IISDefensiveAI.Agent;

/// <summary>
/// Runs a nightly learning job (default 2:00 AM local) to build a latency baseline from recent JSON logs.
/// </summary>
public class NightlyLearningService : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>2:00 AM every day, local time.</summary>
    private static readonly CronExpression NightlySchedule = CronExpression.Parse("0 2 * * *");

    private readonly LogMonitoringOptions _options;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IISController _iisController;
    private readonly ILogger<NightlyLearningService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _schedulerTask;

    public NightlyLearningService(
        IOptions<LogMonitoringOptions> options,
        IHostEnvironment hostEnvironment,
        IISController iisController,
        ILogger<NightlyLearningService> logger)
    {
        _options = options.Value;
        _hostEnvironment = hostEnvironment;
        _iisController = iisController;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _schedulerTask = SchedulerLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_schedulerTask is not null)
        {
            try
            {
                await _schedulerTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown.
            }
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var nextLocal = NightlySchedule.GetNextOccurrence(now, TimeZoneInfo.Local);
                if (nextLocal is null)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = nextLocal.Value - DateTimeOffset.Now;
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation(
                        "Nightly learning job scheduled for {NextRun:o} (in {Delay}).",
                        nextLocal.Value,
                        delay);
                    await Task.Delay(delay, stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                await RunLearningJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly learning scheduler error; retrying in 1 minute.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunLearningJobAsync(CancellationToken cancellationToken)
    {
        var directory = ResolveLogDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning("Learning job skipped: log directory is missing or not configured: {Directory}", directory ?? "(null)");
            return;
        }

        var fileCutoffUtc = DateTime.UtcNow.AddHours(-24);
        var entryCutoff = DateTimeOffset.UtcNow.AddHours(-24);

        var jsonFiles = Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(f) >= fileCutoffUtc;
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

        _logger.LogInformation(
            "Learning job: scanning {FileCount} .json file(s) modified in the last 24 hours under {Directory}.",
            jsonFiles.Count,
            directory);

        var logs = new List<LogEntry>();
        foreach (var path in jsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<LogEntry>(line, JsonOptions);
                    if (entry is null)
                        continue;
                    if (entry.Timestamp >= entryCutoff)
                        logs.Add(entry);
                }
                catch (JsonException)
                {
                    // Skip malformed lines in rolling logs.
                }
            }
        }

        var feedbackAugmentation = _iisController.GetSafeFeedbackAugmentationForBaseline();
        if (feedbackAugmentation.Count > 0)
        {
            logs.AddRange(feedbackAugmentation);
            _logger.LogInformation(
                "Augmenting baseline training with {AugmentCount} synthetic samples from user safe-feedback ({File}).",
                feedbackAugmentation.Count,
                IISController.FalsePositivesFileName);
        }

        var profile = GenerateBaseline(logs);

        var remediationAgg = LoadRemediationEffectivenessAggregate();
        if (remediationAgg.ByRequestPath.Count > 0)
        {
            profile.RemediationEffectivenessByPath = remediationAgg.ByRequestPath;
            _logger.LogInformation(
                "Merged remediation effectiveness into baseline for {PathCount} request path(s) ({Total} audit outcomes).",
                remediationAgg.ByRequestPath.Count,
                remediationAgg.TotalOutcomesRead);
        }

        var outPath = Path.Combine(_hostEnvironment.ContentRootPath, "baseline_profile.json");
        var json = JsonSerializer.Serialize(profile, WriteJsonOptions);
        await File.WriteAllTextAsync(outPath, json, cancellationToken);

        _logger.LogInformation(
            "Baseline profile written to {Path}. Paths analyzed: {PathCount}, Average global latency: {AvgGlobalMs:F2} ms.",
            outPath,
            profile.Paths.Count,
            profile.AverageGlobalLatencyMs);

        await WriteRemediationEffectivenessSidecarAsync(remediationAgg, cancellationToken);
    }

    private RemediationEffectivenessAggregate LoadRemediationEffectivenessAggregate()
    {
        var agg = new RemediationEffectivenessAggregate { GeneratedAtUtc = DateTimeOffset.UtcNow };
        var path = Path.Combine(_hostEnvironment.ContentRootPath, PostActionAuditService.OutcomesFileName);
        if (!File.Exists(path))
            return agg;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var r = JsonSerializer.Deserialize<RemediationOutcomeLogRecord>(line, JsonOptions);
                if (r is null || string.IsNullOrEmpty(r.RequestPath))
                    continue;

                agg.TotalOutcomesRead++;
                if (!agg.ByRequestPath.TryGetValue(r.RequestPath, out var stats))
                {
                    stats = new PathRemediationStats();
                    agg.ByRequestPath[r.RequestPath] = stats;
                }

                if (string.Equals(r.Outcome, "effective", StringComparison.OrdinalIgnoreCase))
                    stats.RecycleEffective++;
                else if (string.Equals(r.Outcome, "ineffective_escalating", StringComparison.OrdinalIgnoreCase))
                    stats.RecycleIneffectiveEscalating++;
            }
            catch (JsonException)
            {
                // Skip corrupt lines.
            }
        }

        return agg;
    }

    private async Task WriteRemediationEffectivenessSidecarAsync(RemediationEffectivenessAggregate agg, CancellationToken cancellationToken)
    {
        var outPath = Path.Combine(_hostEnvironment.ContentRootPath, "remediation_effectiveness.json");
        var json = JsonSerializer.Serialize(agg, WriteJsonOptions);
        await File.WriteAllTextAsync(outPath, json, cancellationToken);

        if (agg.TotalOutcomesRead > 0)
        {
            _logger.LogInformation(
                "Wrote remediation effectiveness sidecar to {Path} ({Total} outcomes).",
                outPath,
                agg.TotalOutcomesRead);
        }
    }

    /// <summary>
    /// Groups logs by request path and computes mean, sample standard deviation, and P95 of <see cref="LogEntry.LogProperties.ElapsedMilliseconds"/>.
    /// </summary>
    public BaselineProfile GenerateBaseline(IEnumerable<LogEntry> logs)
    {
        var samples = logs
            .Where(e => e.Properties?.ElapsedMilliseconds is not null)
            .Select(e =>
            {
                var path = e.Properties!.RequestPath;
                var key = string.IsNullOrWhiteSpace(path) ? "(unknown)" : path.Trim();
                return (Path: key, Ms: e.Properties!.ElapsedMilliseconds!.Value);
            })
            .ToList();

        var profile = new BaselineProfile
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Paths = new Dictionary<string, PathBaselineStats>(StringComparer.OrdinalIgnoreCase),
        };

        var globalLatencies = new List<double>();

        foreach (var group in samples.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.Select(x => x.Ms).OrderBy(x => x).ToList();
            if (values.Count == 0)
                continue;

            globalLatencies.AddRange(values);

            var mean = values.Average();
            profile.Paths[group.Key] = new PathBaselineStats
            {
                MeanMs = mean,
                StdDevMs = SampleStandardDeviation(values, mean),
                P95Ms = Percentile(values, 0.95),
                SampleCount = values.Count,
            };
        }

        profile.AverageGlobalLatencyMs = globalLatencies.Count > 0 ? globalLatencies.Average() : 0;
        return profile;
    }

    private static double SampleStandardDeviation(IReadOnlyList<double> sortedOrAny, double mean)
    {
        var n = sortedOrAny.Count;
        if (n < 2)
            return 0;

        var sumSq = 0.0;
        for (var i = 0; i < n; i++)
        {
            var d = sortedOrAny[i] - mean;
            sumSq += d * d;
        }

        return Math.Sqrt(sumSq / (n - 1));
    }

    /// <summary>Linear interpolation of rank-based percentile on sorted values.</summary>
    private static double Percentile(IReadOnlyList<double> sortedValues, double p)
    {
        if (sortedValues.Count == 0)
            return 0;
        if (sortedValues.Count == 1)
            return sortedValues[0];

        var rank = p * (sortedValues.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
            return sortedValues[low];

        var w = rank - low;
        return sortedValues[low] * (1 - w) + sortedValues[high] * w;
    }

    private string? ResolveLogDirectory()
    {
        var path = _options.LogDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(path))
            return null;

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, path));
    }
}
