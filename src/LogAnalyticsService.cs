using System.Text.Json;
using IISDefensiveAI.Agent.Models;
using Microsoft.Extensions.Options;

namespace IISDefensiveAI.Agent;

public class LogAnalyticsService
{
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
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<LogAnalyticsService> _logger;
    private readonly SemaphoreSlim _historyWriteGate = new(1, 1);

    public LogAnalyticsService(
        IOptions<LogAnalyticsOptions> options,
        IOptions<LogMonitoringOptions> monitoringOptions,
        IHostEnvironment hostEnvironment,
        ILogger<LogAnalyticsService> logger)
    {
        _options = options.Value;
        _monitoringOptions = monitoringOptions.Value;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public async Task<List<ApiAnalyticsResponse>> GetStatsAsync(CancellationToken cancellationToken)
    {
        var directory = ResolveLogDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Log analytics skipped: directory missing or not configured (LogAnalytics:LogDirectory).");
            var empty = new List<ApiAnalyticsResponse>();
            await TryWriteAnalyticsHistorySnapshotAsync(
                    empty,
                    totalHours: 0,
                    logWindowStartUtc: null,
                    logWindowEndUtc: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return empty;
        }

        var filter = string.IsNullOrWhiteSpace(_options.FileFilter) ? "*.json" : _options.FileFilter.Trim();
        var files = Directory.EnumerateFiles(directory, filter, SearchOption.TopDirectoryOnly).ToList();

        var accumulators = new Dictionary<string, PathAccumulator>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? globalMinTimestamp = null;
        DateTimeOffset? globalMaxTimestamp = null;

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
                        if (globalMinTimestamp is null || entry.Timestamp < globalMinTimestamp)
                            globalMinTimestamp = entry.Timestamp;
                        if (globalMaxTimestamp is null || entry.Timestamp > globalMaxTimestamp)
                            globalMaxTimestamp = entry.Timestamp;
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Log analytics: could not read file {File}; skipping.", path);
            }
        }

        var totalHours = 0.0;
        if (globalMinTimestamp is { } lo && globalMaxTimestamp is { } hi)
        {
            var span = hi - lo;
            totalHours = span.TotalHours;
            if (totalHours <= 0)
                totalHours = 0;
        }

        var results = accumulators
            .Select(kvp => new ApiAnalyticsResponse
            {
                RequestPath = kvp.Key,
                CallCount = kvp.Value.CallCount,
                ErrorCount = kvp.Value.ErrorCount,
                ErrorBreakdown = kvp.Value.ErrorTypeCounts
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new ErrorDetail { ErrorType = x.Key, Count = x.Value })
                    .ToArray(),
                AverageCallsPerHour = totalHours > 0 ? kvp.Value.CallCount / totalHours : 0,
                AverageElapsedMs = kvp.Value.ElapsedSampleCount > 0
                    ? kvp.Value.SumElapsed / kvp.Value.ElapsedSampleCount
                    : 0,
                MaxElapsedMs = kvp.Value.ElapsedSampleCount > 0 ? kvp.Value.MaxElapsed : 0,
            })
            .OrderByDescending(r => r.CallCount - r.ErrorCount)
            .ThenByDescending(r => r.CallCount)
            .ThenBy(r => r.RequestPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await TryWriteAnalyticsHistorySnapshotAsync(
                results,
                totalHours,
                globalMinTimestamp,
                globalMaxTimestamp,
                cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    private async Task TryWriteAnalyticsHistorySnapshotAsync(
        IReadOnlyList<ApiAnalyticsResponse> stats,
        double totalHours,
        DateTimeOffset? logWindowStartUtc,
        DateTimeOffset? logWindowEndUtc,
        CancellationToken cancellationToken)
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
                StatsCount = stats.Count,
                GlobalObservedHours = totalHours,
                LogSampleWindowStartUtc = logWindowStartUtc,
                LogSampleWindowEndUtc = logWindowEndUtc,
                LogAnalyticsDirectory = ResolveLogDirectory(),
                Stats = stats.ToList(),
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

        public int StatsCount { get; init; }

        public double GlobalObservedHours { get; init; }

        public DateTimeOffset? LogSampleWindowStartUtc { get; init; }

        public DateTimeOffset? LogSampleWindowEndUtc { get; init; }

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
