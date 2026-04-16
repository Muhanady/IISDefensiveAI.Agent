using System.Text.Json;
using IISDefensiveAI.Agent.Models;
using Microsoft.Extensions.Options;

namespace IISDefensiveAI.Agent;

public class LogAnalyticsService
{
    private const double FallbackLongLatencyThresholdMs = 500d;

    private const double MinDensityHoursForThroughput = 0.01;

    private const string AnalyticsFilterApplied = "ErrorsOnly_Or_LatencyOutliers";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions HistorySnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly LogAnalyticsOptions _options;
    private readonly LogMonitoringOptions _monitoringOptions;
    private readonly LogMonitoringService _logMonitoring;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<LogAnalyticsService> _logger;
    private readonly SemaphoreSlim _historyWriteGate = new(1, 1);

    public LogAnalyticsService(
        IOptions<LogAnalyticsOptions> options,
        IOptions<LogMonitoringOptions> monitoringOptions,
        LogMonitoringService logMonitoring,
        IHostEnvironment hostEnvironment,
        ILogger<LogAnalyticsService> logger)
    {
        _options = options.Value;
        _monitoringOptions = monitoringOptions.Value;
        _logMonitoring = logMonitoring;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public async Task<AnalyticsReport> GetStatsAsync(CancellationToken cancellationToken)
    {
        var directory = ResolveLogDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Log analytics skipped: directory missing or not configured (LogAnalytics:LogDirectory).");
            var emptyReport = new AnalyticsReport
            {
                LogSampleWindowStartUtc = null,
                LogSampleWindowEndUtc = null,
                GlobalDensityHours = 0,
                FilterApplied = AnalyticsFilterApplied,
                Stats = Array.Empty<ApiAnalyticsResponse>(),
            };
            await TryWriteAnalyticsHistorySnapshotAsync(emptyReport, cancellationToken).ConfigureAwait(false);
            return emptyReport;
        }

        var filter = string.IsNullOrWhiteSpace(_options.FileFilter) ? "*.json" : _options.FileFilter.Trim();
        var files = Directory.EnumerateFiles(directory, filter, SearchOption.TopDirectoryOnly).ToList();

        var accumulators = new Dictionary<string, PathAccumulator>(StringComparer.OrdinalIgnoreCase);
        var overallMin = DateTime.MaxValue;
        var overallMax = DateTime.MinValue;

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    LogEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<LogEntry>(line, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (entry is null)
                        continue;

                    var pathKey = RequestPathNormalizer.Normalize(entry.Properties?.RequestPath);
                    if (!accumulators.TryGetValue(pathKey, out var acc))
                    {
                        acc = new PathAccumulator();
                        accumulators[pathKey] = acc;
                    }

                    acc.CallCount++;

                    if (entry.Properties?.ElapsedMilliseconds is { } ms && double.IsFinite(ms))
                    {
                        acc.SumElapsed += ms;
                        acc.ElapsedSampleCount++;
                        if (ms > acc.MaxElapsed)
                            acc.MaxElapsed = ms;
                    }

                    if (IsError(entry))
                    {
                        acc.ErrorCount++;
                        var errorType = GetErrorType(entry);
                        acc.ErrorTypeCounts.TryGetValue(errorType, out var ec);
                        acc.ErrorTypeCounts[errorType] = ec + 1;
                    }

                    if (entry.Timestamp != default)
                    {
                        var tsUtc = entry.Timestamp.UtcDateTime;
                        if (tsUtc < overallMin)
                            overallMin = tsUtc;
                        if (tsUtc > overallMax)
                            overallMax = tsUtc;
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Log analytics: could not read file {File}; skipping.", path);
            }
        }

        var hasTimestampWindow = overallMin != DateTime.MaxValue;
        var globalDensityHours = 0d;
        if (hasTimestampWindow)
            globalDensityHours = (overallMax - overallMin).TotalHours;

        var densityHoursForThroughput = Math.Max(globalDensityHours, MinDensityHoursForThroughput);

        var results = accumulators
            .Where(kvp =>
            {
                _logMonitoring.TryGetPathBaselineStats(kvp.Key, out var pathBaseline);
                var longThresholdMs = GetLongLatencyThresholdMs(pathBaseline);
                var maxElapsedMs = kvp.Value.ElapsedSampleCount > 0 ? kvp.Value.MaxElapsed : 0d;
                return kvp.Value.ErrorCount > 0 || maxElapsedMs > longThresholdMs;
            })
            .Select(kvp =>
            {
                var callsPerHour = kvp.Value.CallCount / densityHoursForThroughput;
                var avgMs = kvp.Value.ElapsedSampleCount > 0
                    ? kvp.Value.SumElapsed / kvp.Value.ElapsedSampleCount
                    : 0d;
                var maxMs = kvp.Value.ElapsedSampleCount > 0 ? kvp.Value.MaxElapsed : 0d;

                return new ApiAnalyticsResponse
                {
                    RequestPath = kvp.Key,
                    CallCount = kvp.Value.CallCount,
                    ErrorCount = kvp.Value.ErrorCount,
                    ErrorBreakdown = kvp.Value.ErrorTypeCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new ErrorDetail { ErrorType = x.Key, Count = x.Value })
                        .ToArray(),
                    AverageCallsPerHour = Math.Round(callsPerHour, 1, MidpointRounding.AwayFromZero),
                    AverageElapsedMs = Math.Round(avgMs, 1, MidpointRounding.AwayFromZero),
                    MaxElapsedMs = Math.Round(maxMs, 0, MidpointRounding.AwayFromZero),
                };
            })
            .OrderByDescending(r => r.CallCount - r.ErrorCount)
            .ThenByDescending(r => r.CallCount)
            .ThenBy(r => r.RequestPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var report = new AnalyticsReport
        {
            LogSampleWindowStartUtc = hasTimestampWindow ? overallMin : null,
            LogSampleWindowEndUtc = hasTimestampWindow ? overallMax : null,
            GlobalDensityHours = globalDensityHours,
            FilterApplied = AnalyticsFilterApplied,
            Stats = results,
        };

        await TryWriteAnalyticsHistorySnapshotAsync(report, cancellationToken).ConfigureAwait(false);

        return report;
    }

    /// <summary>Latency outlier threshold: mean + 2σ when a baseline row exists; otherwise <see cref="FallbackLongLatencyThresholdMs"/>.</summary>
    private static double GetLongLatencyThresholdMs(PathBaselineStats? pathBaseline) =>
        pathBaseline is not null
            ? pathBaseline.MeanMs + 2 * pathBaseline.StdDevMs
            : FallbackLongLatencyThresholdMs;

    private async Task TryWriteAnalyticsHistorySnapshotAsync(AnalyticsReport report, CancellationToken cancellationToken)
    {
        await _historyWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = ResolveAnalyticsHistoryDirectory();
            Directory.CreateDirectory(dir);

            var filePath = ResolveUniqueAnalyticsHistoryFilePath(dir);
            var document = new AnalyticsHistorySnapshotDocument
            {
                CalculationTimestampUtc = DateTimeOffset.UtcNow,
                SchemaVersion = "1.0",
                FilterApplied = report.FilterApplied,
                StatsCount = report.Stats.Count,
                LogSampleWindowStartUtc = report.LogSampleWindowStartUtc,
                LogSampleWindowEndUtc = report.LogSampleWindowEndUtc,
                GlobalDensityHours = report.GlobalDensityHours,
                LogAnalyticsDirectory = ResolveLogDirectory(),
                Stats = report.Stats.ToList(),
            };

            await using var stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, document, HistorySnapshotJsonOptions, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Analytics snapshot (.json) saved to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write analytics history snapshot.");
        }
        finally
        {
            _historyWriteGate.Release();
        }
    }

    /// <summary>Root JSON shape for a single analytics snapshot file.</summary>
    private sealed class AnalyticsHistorySnapshotDocument
    {
        public DateTimeOffset CalculationTimestampUtc { get; init; }

        public string SchemaVersion { get; init; } = "1.0";

        public string FilterApplied { get; init; } = string.Empty;

        public int StatsCount { get; init; }

        public DateTime? LogSampleWindowStartUtc { get; init; }

        public DateTime? LogSampleWindowEndUtc { get; init; }

        public double GlobalDensityHours { get; init; }

        /// <summary>Resolved <see cref="LogAnalyticsOptions.LogDirectory"/> used for this run (may be null if unconfigured).</summary>
        public string? LogAnalyticsDirectory { get; init; }

        public List<ApiAnalyticsResponse> Stats { get; init; } = [];
    }

    private string ResolveAnalyticsHistoryDirectory()
    {
        var raw = _monitoringOptions.DiagnosisDirectory?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(raw)
            ? _hostEnvironment.ContentRootPath
            : Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, raw));
    }

    /// <summary>
    /// Builds <c>analytics_history_yyyyMMdd_HHmmss.json</c> under <paramref name="directory"/>; if that name exists, appends <c>_2</c>, <c>_3</c>, etc. before the extension.
    /// </summary>
    private static string ResolveUniqueAnalyticsHistoryFilePath(string directory)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var baseName = $"analytics_history_{stamp}";
        var candidate = Path.Combine(directory, baseName + ".json");
        if (!File.Exists(candidate))
            return candidate;

        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{baseName}_{i}.json");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}.json");
    }

    private static bool IsError(LogEntry entry)
    {
        if (entry.Properties?.StatusCode is >= 500)
            return true;

        if (string.Equals(entry.Level, "Error", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(entry.Exception);
    }

    /// <summary>First line of <see cref="LogEntry.Exception"/> when present; otherwise <see cref="LogEntry.MessageTemplate"/>.</summary>
    private static string GetErrorType(LogEntry entry)
    {
        var ex = entry.Exception?.Trim();
        if (!string.IsNullOrEmpty(ex))
        {
            ReadOnlySpan<char> span = ex.AsSpan();
            var idx = span.IndexOfAny('\r', '\n');
            if (idx >= 0)
                span = span[..idx];
            var first = span.Trim().ToString();
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        var mt = entry.MessageTemplate?.Trim();
        return string.IsNullOrEmpty(mt) ? "(unknown)" : mt;
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

    private sealed class PathAccumulator
    {
        public int CallCount;
        public double SumElapsed;
        public int ElapsedSampleCount;
        public double MaxElapsed;
        public int ErrorCount;
        public Dictionary<string, int> ErrorTypeCounts { get; } = new(StringComparer.Ordinal);
    }
}
