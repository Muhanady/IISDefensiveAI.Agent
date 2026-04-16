using System.Globalization;
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
            return new AnalyticsReport
            {
                LogSampleWindowStartUtc = null,
                LogSampleWindowEndUtc = null,
                GlobalDensityHours = 0,
                FilterApplied = AnalyticsFilterApplied,
                Stats = Array.Empty<ApiAnalyticsResponse>(),
            };
        }

        var filter = string.IsNullOrWhiteSpace(_options.FileFilter) ? "*.json" : _options.FileFilter.Trim();
        var files = Directory.EnumerateFiles(directory, filter, SearchOption.TopDirectoryOnly).ToList();

        var globalAccumulators = new Dictionary<string, PathAccumulator>(StringComparer.OrdinalIgnoreCase);
        var overallMin = DateTime.MaxValue;
        var overallMax = DateTime.MinValue;

        await _historyWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Dictionary<string, PathAccumulator> fileAccumulators;
                DateTime fileMin;
                DateTime fileMax;
                try
                {
                    (fileAccumulators, fileMin, fileMax) =
                        await ParseLogFileIntoAccumulatorsAsync(filePath, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Log analytics: could not read file {File}; skipping.", filePath);
                    continue;
                }

                var hasFileWindow = fileMin != DateTime.MaxValue;
                if (hasFileWindow)
                {
                    if (fileMin < overallMin)
                        overallMin = fileMin;
                    if (fileMax > overallMax)
                        overallMax = fileMax;
                }

                var fileDensityRaw = 0d;
                if (hasFileWindow)
                    fileDensityRaw = (fileMax - fileMin).TotalHours;

                var fileThroughputDenom = Math.Max(fileDensityRaw, MinDensityHoursForThroughput);

                var perFileStats = BuildFilteredStatsList(fileAccumulators, fileThroughputDenom);
                var perFileReport = new AnalyticsReport
                {
                    LogSampleWindowStartUtc = hasFileWindow ? fileMin : null,
                    LogSampleWindowEndUtc = hasFileWindow ? fileMax : null,
                    GlobalDensityHours = Math.Round(fileDensityRaw, 2, MidpointRounding.AwayFromZero),
                    FilterApplied = AnalyticsFilterApplied,
                    Stats = perFileStats,
                };

                var historyDir = ResolveAnalyticsHistoryDirectory();
                Directory.CreateDirectory(historyDir);
                var perFileOutPath = Path.Combine(
                    historyDir,
                    $"{SanitizeFileName(Path.GetFileNameWithoutExtension(filePath) ?? Path.GetFileName(filePath))}_analytics.json");
                var perFileDoc = ToSnapshotDocument(perFileReport);
                await WriteJsonDocumentAsync(perFileOutPath, perFileDoc, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Per-file analytics saved to {Path}", perFileOutPath);

                MergeAccumulators(globalAccumulators, fileAccumulators);
            }

            var hasGlobalWindow = overallMin != DateTime.MaxValue;
            var globalDensityRaw = 0d;
            if (hasGlobalWindow)
                globalDensityRaw = (overallMax - overallMin).TotalHours;

            var globalThroughputDenom = Math.Max(globalDensityRaw, MinDensityHoursForThroughput);

            var summaryStats = BuildFilteredStatsList(globalAccumulators, globalThroughputDenom, includeSourceFile: true);
            var summaryReport = new AnalyticsReport
            {
                LogSampleWindowStartUtc = hasGlobalWindow ? overallMin : null,
                LogSampleWindowEndUtc = hasGlobalWindow ? overallMax : null,
                GlobalDensityHours = Math.Round(globalDensityRaw, 2, MidpointRounding.AwayFromZero),
                FilterApplied = AnalyticsFilterApplied,
                Stats = summaryStats,
            };

            var summaryDir = ResolveAnalyticsHistoryDirectory();
            Directory.CreateDirectory(summaryDir);
            var summaryOutPath = Path.Combine(
                summaryDir,
                $"analytics_summary_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.json");
            var summaryDoc = ToSnapshotDocument(summaryReport);
            await WriteJsonDocumentAsync(summaryOutPath, summaryDoc, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Analytics summary saved to {Path}", summaryOutPath);

            return summaryReport;
        }
        finally
        {
            _historyWriteGate.Release();
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "log";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]))
                chars[i] = '_';
        }

        return new string(chars);
    }

    private static async Task<(Dictionary<string, PathAccumulator> Accumulators, DateTime FileMin, DateTime FileMax)>
        ParseLogFileIntoAccumulatorsAsync(string filePath, CancellationToken cancellationToken)
    {
        var accumulators = new Dictionary<string, PathAccumulator>(StringComparer.OrdinalIgnoreCase);
        var fileMin = DateTime.MaxValue;
        var fileMax = DateTime.MinValue;

        using var stream = new FileStream(
            filePath,
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

            var isError = IsError(entry);
            if (isError)
            {
                acc.ErrorCount++;
                var errorType = GetErrorType(entry);
                acc.ErrorTypeCounts.TryGetValue(errorType, out var ec);
                acc.ErrorTypeCounts[errorType] = ec + 1;
            }

            if (entry.Timestamp != default)
            {
                var tsUtc = entry.Timestamp.UtcDateTime;
                if (tsUtc < fileMin)
                    fileMin = tsUtc;
                if (tsUtc > fileMax)
                    fileMax = tsUtc;
            }

            var sourceFile = Path.GetFileName(filePath);
            acc.SourceContributions.TryGetValue(sourceFile, out var source);
            var sourceMaxElapsedMs = source.MaxElapsedMs;
            if (entry.Properties?.ElapsedMilliseconds is { } sourceMs && double.IsFinite(sourceMs) && sourceMs > sourceMaxElapsedMs)
                sourceMaxElapsedMs = sourceMs;
            source = new SourceContribution
            {
                CallCount = source.CallCount + 1,
                ErrorCount = source.ErrorCount + (isError ? 1 : 0),
                MaxElapsedMs = sourceMaxElapsedMs,
            };
            acc.SourceContributions[sourceFile] = source;
        }

        return (accumulators, fileMin, fileMax);
    }

    private static void MergeAccumulators(
        Dictionary<string, PathAccumulator> global,
        Dictionary<string, PathAccumulator> fileLocal)
    {
        foreach (var (key, src) in fileLocal)
        {
            if (!global.TryGetValue(key, out var dst))
            {
                dst = new PathAccumulator();
                global[key] = dst;
            }

            dst.CallCount += src.CallCount;
            dst.SumElapsed += src.SumElapsed;
            dst.ElapsedSampleCount += src.ElapsedSampleCount;
            dst.ErrorCount += src.ErrorCount;
            if (src.MaxElapsed > dst.MaxElapsed)
                dst.MaxElapsed = src.MaxElapsed;

            foreach (var (errorType, count) in src.ErrorTypeCounts)
            {
                dst.ErrorTypeCounts.TryGetValue(errorType, out var existing);
                dst.ErrorTypeCounts[errorType] = existing + count;
            }

            foreach (var (sourceFile, sourceContribution) in src.SourceContributions)
            {
                dst.SourceContributions.TryGetValue(sourceFile, out var existing);
                dst.SourceContributions[sourceFile] = new SourceContribution
                {
                    CallCount = existing.CallCount + sourceContribution.CallCount,
                    ErrorCount = existing.ErrorCount + sourceContribution.ErrorCount,
                    MaxElapsedMs = Math.Max(existing.MaxElapsedMs, sourceContribution.MaxElapsedMs),
                };
            }
        }
    }

    private List<ApiAnalyticsResponse> BuildFilteredStatsList(
        Dictionary<string, PathAccumulator> accumulators,
        double densityHoursForThroughput,
        bool includeSourceFile = false)
    {
        return accumulators
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
                    SourceFile = includeSourceFile ? ResolveTopSourceFile(kvp.Value.SourceContributions) : null,
                };
            })
            .OrderByDescending(r => r.CallCount - r.ErrorCount)
            .ThenByDescending(r => r.CallCount)
            .ThenBy(r => r.RequestPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private AnalyticsHistorySnapshotDocument ToSnapshotDocument(AnalyticsReport report) =>
        new()
        {
            CalculationTimestampUtc = DateTimeOffset.UtcNow,
            StatsCount = report.Stats.Count,
            LogAnalyticsDirectory = ResolveLogDirectory(),
            Stats = report.Stats.ToList(),
        };

    private static string? ResolveTopSourceFile(Dictionary<string, SourceContribution> sourceContributions)
    {
        if (sourceContributions.Count == 0)
            return null;

        var hasErrors = sourceContributions.Values.Any(v => v.ErrorCount > 0);
        if (hasErrors)
        {
            return sourceContributions
                .OrderByDescending(x => x.Value.ErrorCount)
                .ThenByDescending(x => x.Value.MaxElapsedMs)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key)
                .FirstOrDefault();
        }

        return sourceContributions
            .OrderByDescending(x => x.Value.MaxElapsedMs)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .FirstOrDefault();
    }

    private async Task WriteJsonDocumentAsync(
        string filePath,
        AnalyticsHistorySnapshotDocument document,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, document, HistorySnapshotJsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Latency outlier threshold: mean + 2σ when a baseline row exists; otherwise <see cref="FallbackLongLatencyThresholdMs"/>.</summary>
    private static double GetLongLatencyThresholdMs(PathBaselineStats? pathBaseline) =>
        pathBaseline is not null
            ? pathBaseline.MeanMs + 2 * pathBaseline.StdDevMs
            : FallbackLongLatencyThresholdMs;

    /// <summary>Root JSON shape for per-file analytics and directory summary exports.</summary>
    private sealed class AnalyticsHistorySnapshotDocument
    {
        public DateTimeOffset CalculationTimestampUtc { get; init; }

        public int StatsCount { get; init; }

        /// <summary>Resolved log analytics directory for this run.</summary>
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
        public Dictionary<string, SourceContribution> SourceContributions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly struct SourceContribution
    {
        public int CallCount { get; init; }
        public int ErrorCount { get; init; }
        public double MaxElapsedMs { get; init; }
    }
}
