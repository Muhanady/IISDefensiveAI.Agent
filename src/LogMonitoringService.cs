using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IISDefensiveAI.Agent.Models;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace IISDefensiveAI.Agent;

public class LogMonitoringService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly MLContext _mlContext = new(seed: 0);
    private readonly List<float> _elapsedMillisecondsBuffer;
    private readonly object _mlBufferLock = new();

    private readonly LogMonitoringOptions _options;
    private readonly IISController _iisController;
    private readonly PostActionAuditService _postActionAudit;
    private readonly AnomalyTelemetry _anomalyTelemetry;
    private readonly DiagnosticReasoningService _diagnosticReasoning;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<LogMonitoringService> _logger;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly List<DateTimeOffset> _sqlTimeoutSignalsUtc = new();
    private readonly object _sqlTimeoutLock = new();
    private readonly ConcurrentDictionary<string, long> _filePositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StringBuilder> _lineBuffers = new(StringComparer.OrdinalIgnoreCase);

    private BaselineProfile? _baseline;
    private readonly object _baselineLock = new();

    public LogMonitoringService(
        IOptions<LogMonitoringOptions> options,
        IISController iisController,
        PostActionAuditService postActionAudit,
        AnomalyTelemetry anomalyTelemetry,
        DiagnosticReasoningService diagnosticReasoning,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<LogMonitoringService> logger,
        IHostEnvironment hostEnvironment)
    {
        _options = options.Value;
        _elapsedMillisecondsBuffer = new List<float>(_options.ElapsedMsBufferCapacity);
        _iisController = iisController;
        _postActionAudit = postActionAudit;
        _anomalyTelemetry = anomalyTelemetry;
        _diagnosticReasoning = diagnosticReasoning;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
        _hostEnvironment = hostEnvironment;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = ResolveLogDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            _logger.LogError("LogMonitoring:LogDirectory is not configured.");
            return;
        }

        while (!Directory.Exists(directory) && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Log directory does not exist yet: {Directory}. Retrying in 5s.", directory);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        LoadBaseline();

        var baselineWatcher = CreateBaselineProfileWatcher();

        SeedExistingFilePositions(directory);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var watcher = new FileSystemWatcher(directory, _options.FileFilter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };

        void Enqueue(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return;
            try
            {
                fullPath = Path.GetFullPath(fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not normalize path: {Path}", fullPath);
                return;
            }

            channel.Writer.TryWrite(fullPath);
        }

        watcher.Created += (_, e) =>
        {
            try
            {
                var fullPath = Path.GetFullPath(e.FullPath);
                _filePositions[fullPath] = 0;
                _lineBuffers.AddOrUpdate(
                    fullPath,
                    _ => new StringBuilder(),
                    (_, sb) =>
                    {
                        sb.Clear();
                        return sb;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not reset state for new file {Path}", e.FullPath);
            }

            Enqueue(e.FullPath);
        };
        watcher.Changed += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        watcher.Error += (_, e) =>
            _logger.LogError(e.GetException(), "FileSystemWatcher error for {Directory}", directory);

        watcher.EnableRaisingEvents = true;

        var readTask = ReadChannelAsync(channel.Reader, stoppingToken);

        try
        {
            await readTask;
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            baselineWatcher?.Dispose();
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Loads <c>baseline_profile.json</c> from the application content root. Call at startup and when the file changes.</summary>
    public void LoadBaseline()
    {
        var path = Path.Combine(_hostEnvironment.ContentRootPath, "baseline_profile.json");
        if (!File.Exists(path))
        {
            lock (_baselineLock)
                _baseline = null;

            _logger.LogInformation("Baseline profile not found at {Path}; IID spike detection will run for all paths.", path);
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var profile = JsonSerializer.Deserialize<BaselineProfile>(json, JsonOptions);
            if (profile?.Paths is null)
            {
                lock (_baselineLock)
                    _baseline = null;
                _logger.LogWarning("Baseline profile at {Path} deserialized with no paths; ignoring.", path);
                return;
            }

            // System.Text.Json uses ordinal dictionary comparer; rebuild for case-insensitive RequestPath lookup.
            var paths = new Dictionary<string, PathBaselineStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in profile.Paths)
                paths[kv.Key] = kv.Value;
            profile.Paths = paths;

            lock (_baselineLock)
                _baseline = profile;

            _logger.LogInformation(
                "Loaded baseline profile from {Path}: {PathCount} paths, generated {GeneratedUtc:o}.",
                path,
                profile.Paths.Count,
                profile.GeneratedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load baseline profile; existing baseline (if any) retained.");
        }
    }

    private FileSystemWatcher? CreateBaselineProfileWatcher()
    {
        var root = _hostEnvironment.ContentRootPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return null;

        var watcher = new FileSystemWatcher(root, "baseline_profile.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };

        void OnBaselineFileChange(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(Path.GetFileName(e.FullPath), "baseline_profile.json", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                LoadBaseline();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Baseline reload after file system event failed.");
            }
        }

        watcher.Changed += OnBaselineFileChange;
        watcher.Created += OnBaselineFileChange;
        watcher.Renamed += (_, e) =>
        {
            if (string.Equals(Path.GetFileName(e.FullPath), "baseline_profile.json", StringComparison.OrdinalIgnoreCase))
                OnBaselineFileChange(null, e);
        };

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Matches <see cref="NightlyLearningService"/> path grouping.</summary>
    private static string NormalizeRequestPath(string? requestPath) =>
        RequestPathNormalizer.Normalize(requestPath);

    /// <summary>
    /// True when a baseline exists for the request path and latency is within mean ± 2σ (requires ≥2 samples and σ &gt; 0).
    /// </summary>
    private bool IsWithinBaselineExpectedBand(LogEntry entry, double elapsedMs)
    {
        PathBaselineStats? stats;
        lock (_baselineLock)
        {
            if (_baseline?.Paths is null)
                return false;

            var key = NormalizeRequestPath(entry.Properties?.RequestPath);
            if (!_baseline.Paths.TryGetValue(key, out stats))
                return false;
        }

        if (stats.SampleCount < 2 || stats.StdDevMs <= 0)
            return false;

        var low = stats.MeanMs - 2 * stats.StdDevMs;
        var high = stats.MeanMs + 2 * stats.StdDevMs;
        return elapsedMs >= low && elapsedMs <= high;
    }

    private PathBaselineStats? TryGetPathBaselineStats(LogEntry entry)
    {
        lock (_baselineLock)
        {
            if (_baseline?.Paths is null)
                return null;

            var key = NormalizeRequestPath(entry.Properties?.RequestPath);
            return _baseline.Paths.TryGetValue(key, out var stats) ? stats : null;
        }
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

    private void SeedExistingFilePositions(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, _options.FileFilter))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists)
                        _filePositions[info.FullName] = info.Length;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Could not seed position for {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate existing log files in {Directory}", directory);
        }
    }

    private async Task ReadChannelAsync(ChannelReader<string> reader, CancellationToken stoppingToken)
    {
        await foreach (var path in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessFileAsync(path, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing log file {Path}", path);
            }
        }
    }

    /// <summary>Reads newly appended bytes from the file using shared read/write access, then parses complete lines as JSON.</summary>
    private async Task ProcessFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
            return;

        var gate = _fileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ReadNewLinesFromFileAsync(fullPath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReadNewLinesFromFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        var length = stream.Length;
        var position = _filePositions.GetOrAdd(fullPath, _ => length);

        if (length < position)
        {
            position = 0;
            _filePositions[fullPath] = 0;
            if (_lineBuffers.TryGetValue(fullPath, out var resetBuffer))
                resetBuffer.Clear();
        }

        var toRead = length - position;
        if (toRead <= 0)
        {
            _filePositions[fullPath] = length;
            return;
        }

        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[toRead];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset > 0)
            _logger.LogDebug("Read {ByteCount} bytes from {File}", offset, fullPath);

        _filePositions[fullPath] = length;

        var chunk = Encoding.UTF8.GetString(buffer, 0, offset);
        var lineBuffer = _lineBuffers.GetOrAdd(fullPath, _ => new StringBuilder());
        lineBuffer.Append(chunk);

        while (true)
        {
            var working = lineBuffer.ToString();
            var newlineIndex = working.IndexOf('\n');
            if (newlineIndex < 0)
                break;

            var line = working[..newlineIndex].TrimEnd('\r');
            lineBuffer.Remove(0, newlineIndex + 1);

            if (line.Length == 0)
                continue;

            TryDeserializeAndProcessLine(line);
        }
    }

    private void TryDeserializeAndProcessLine(string line)
    {
        try
        {
            // Trim the BOM and other hidden whitespace characters
            var cleanedLine = line.Trim('\uFEFF', '\u200B').Trim();

            if (string.IsNullOrWhiteSpace(cleanedLine))
                return;

            var entry = JsonSerializer.Deserialize<LogEntry>(cleanedLine, JsonOptions);
            if (entry is null)
                return;

            _logger.LogInformation(
                "Internal Heartbeat | Processing Log: {Template} | Latency: {Ms}ms",
                entry.MessageTemplate,
                entry.Properties?.ElapsedMilliseconds);

            ProcessLogEntry(entry);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "JSON Parse Failure. Line starts with: {Start}. Error: {Msg}",
                line.Substring(0, Math.Min(10, line.Length)),
                ex.Message);
        }
    }

    private void ProcessLogEntry(LogEntry entry)
    {
        RecordSqlTimeoutIfPresent(entry);
        _postActionAudit.TryEvaluateLogEntry(entry);

        _logger.LogInformation(
            "MessageTemplate: {MessageTemplate}, ElapsedMilliseconds: {ElapsedMilliseconds}",
            entry.MessageTemplate,
            entry.Properties?.ElapsedMilliseconds);

        var elapsed = entry.Properties?.ElapsedMilliseconds;
        if (elapsed is null)
        {
            _logger.LogDebug("Skipping entry because ElapsedMilliseconds is null. Message: {Message}", entry.MessageTemplate);
            return;
        }

        var value = (float)elapsed.Value;
        if (!float.IsFinite(value))
            return;

        if (IsWithinBaselineExpectedBand(entry, elapsed.Value))
        {
            _logger.LogDebug(
                "ElapsedMilliseconds {Elapsed} within baseline band for path {RequestPath}; skipping IID spike check.",
                elapsed.Value,
                NormalizeRequestPath(entry.Properties?.RequestPath));
            return;
        }

        var shouldNotifyAnomaly = false;
        lock (_mlBufferLock)
        {
            _elapsedMillisecondsBuffer.Add(value);
            while (_elapsedMillisecondsBuffer.Count > _options.ElapsedMsBufferCapacity)
                _elapsedMillisecondsBuffer.RemoveAt(0);

            _logger.LogInformation("Added to buffer. Current count: {Count}/{Capacity}", _elapsedMillisecondsBuffer.Count, _options.ElapsedMsBufferCapacity);

            if (_elapsedMillisecondsBuffer.Count >= _options.ElapsedMsBufferCapacity)
                shouldNotifyAnomaly = TryDetectSpikeWithIidSpikeDetector(value, out var isSpike) && isSpike;
        }

        if (shouldNotifyAnomaly)
        {
            var pathKey = NormalizeRequestPath(entry.Properties?.RequestPath);
            var suppressedBySafe = _iisController.IsMarkedSafe(pathKey, elapsed.Value);
            _anomalyTelemetry.Record(entry, pathKey, elapsed.Value, suppressedBySafe);

            if (suppressedBySafe)
            {
                _logger.LogInformation(
                    "Anomaly reaction skipped: pattern marked safe in {File} (path {RequestPath}, latency {LatencyMs} ms).",
                    IISController.FalsePositivesFileName,
                    pathKey,
                    elapsed.Value);
                return;
            }

            OnAnomalyDetected(entry);
        }
    }

    /// <summary>
    /// Fits an <see cref="IidSpikeDetector"/> pipeline (via <see cref="TimeSeriesCatalog.DetectIidSpike"/>) on the current buffer and returns whether the latest observation is flagged.
    /// </summary>
    private bool TryDetectSpikeWithIidSpikeDetector(float latestValue, out bool isSpike)
    {
        isSpike = false;
        try
        {
            var series = new List<ElapsedObservation>(_elapsedMillisecondsBuffer.Count);
            foreach (var v in _elapsedMillisecondsBuffer)
                series.Add(new ElapsedObservation { Value = v });

            var dataView = _mlContext.Data.LoadFromEnumerable(series);

            // IidSpikeEstimator → IidSpikeDetector after Fit; 95% confidence per requirement.
            var estimator = _mlContext.Transforms.DetectIidSpike(
                outputColumnName: nameof(SpikePredictionRow.Prediction),
                inputColumnName: nameof(ElapsedObservation.Value),
                confidence: 95d,
                pvalueHistoryLength: _options.SpikePValueHistoryLength,
                side: AnomalySide.TwoSided);

            ITransformer model = estimator.Fit(dataView);
            var scored = model.Transform(dataView);

            SpikePredictionRow? last = null;
            foreach (var row in _mlContext.Data.CreateEnumerable<SpikePredictionRow>(scored, reuseRowObject: false))
                last = row;

            if (last?.Prediction is not { Length: >= 1 })
                return true;

            // [0] = alert: non-zero indicates a spike (see ML.NET IID spike output column docs).
            isSpike = Math.Abs(last.Prediction[0]) > double.Epsilon;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ML.NET IID spike detection failed for ElapsedMilliseconds={Value}", latestValue);
            return false;
        }
    }

    protected virtual void OnAnomalyDetected(LogEntry entry)
    {
        _logger.LogWarning(
            "Anomaly (spike) detected in ElapsedMilliseconds. MessageTemplate: {MessageTemplate}, ElapsedMilliseconds: {ElapsedMilliseconds}",
            entry.MessageTemplate,
            entry.Properties?.ElapsedMilliseconds);

        if (!IsCriticalAnomaly())
            return;

        _logger.LogWarning(
            "Critical anomaly: repeated SQL timeout signals within lookback were followed by a latency spike. MessageTemplate: {MessageTemplate}",
            entry.MessageTemplate);

        if (_options.EnableAutoRca)
            ScheduleCriticalAnomalyRootCauseLogging(entry);

        var normalizedPath = RequestPathNormalizer.Normalize(entry.Properties?.RequestPath);
        if (string.Equals(normalizedPath, "(unknown)", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Critical anomaly: request path is unknown; cannot resolve IIS application pool for remediation.");
            return;
        }

        var poolName = _iisController.GetAppPoolNameForPath(normalizedPath);
        if (string.IsNullOrEmpty(poolName))
        {
            _logger.LogWarning(
                "Critical anomaly: no IIS application matched request path {RequestPath}; skipping recycle.",
                normalizedPath);
            return;
        }

        var authorized = _options.AuthorizedAppPools ?? new List<string>();
        var isAuthorized = authorized.Any(p =>
            !string.IsNullOrWhiteSpace(p) && string.Equals(p.Trim(), poolName, StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            if (authorized.Count == 0)
            {
                _logger.LogWarning(
                    "Critical anomaly: resolved pool {PoolName} for path {RequestPath}, but AuthorizedAppPools is empty; skipping recycle.",
                    poolName,
                    normalizedPath);
            }
            else
            {
                _logger.LogWarning(
                    "Critical anomaly: resolved pool {PoolName} for path {RequestPath} is not in AuthorizedAppPools; skipping recycle.",
                    poolName,
                    normalizedPath);
            }

            return;
        }

        var status = _iisController.GetAppPoolStatus(poolName);
        _logger.LogWarning(
            "Critical anomaly: application pool {AppPoolName} status is {Status} before remediation.",
            poolName,
            status);

        var recycled = _iisController.RecycleAppPool(poolName);
        if (recycled)
        {
            var baselineStats = TryGetPathBaselineStats(entry);
            _postActionAudit.BeginAfterSuccessfulRecycle(
                entry,
                baselineStats,
                entry.Properties?.ElapsedMilliseconds,
                poolName,
                status);
        }
    }

    /// <summary>Runs Ollama RCA in the background so IIS remediation is not blocked.</summary>
    private void ScheduleCriticalAnomalyRootCauseLogging(LogEntry entry)
    {
        var stopping = _hostApplicationLifetime.ApplicationStopping;
        var contentRoot = _hostEnvironment.ContentRootPath;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _diagnosticReasoning.AppendRcaToDiagnosticsLogAsync(entry, contentRoot, "diagnostics_rca.log", stopping);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to append diagnostic RCA for a critical anomaly.");
                }
            },
            stopping);
    }

    /// <summary>Tracks log lines that look like SQL/database timeouts for critical-anomaly correlation with latency spikes.</summary>
    private void RecordSqlTimeoutIfPresent(LogEntry entry)
    {
        if (!LogEntryDiagnostics.IndicatesSqlTimeout(entry))
            return;

        lock (_sqlTimeoutLock)
        {
            PruneSqlTimeoutHistory_NoLock();
            _sqlTimeoutSignalsUtc.Add(DateTimeOffset.UtcNow);
        }
    }

    private void PruneSqlTimeoutHistory_NoLock()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, _options.SqlTimeoutLookbackMinutes));
        _sqlTimeoutSignalsUtc.RemoveAll(t => t < cutoff);
    }

    /// <summary>
    /// Critical = latency spike (already detected) plus repeated SQL timeout signatures in the configured lookback window.
    /// </summary>
    private bool IsCriticalAnomaly()
    {
        lock (_sqlTimeoutLock)
        {
            PruneSqlTimeoutHistory_NoLock();
            return _sqlTimeoutSignalsUtc.Count >= Math.Max(1, _options.SqlTimeoutsRequiredForCritical);
        }
    }

    private sealed class ElapsedObservation
    {
        public float Value { get; set; }
    }

    private sealed class SpikePredictionRow
    {
        [VectorType(3)]
        public double[]? Prediction { get; set; }
    }
}
