using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.Administration;
using IISDefensiveAI.Agent.Models;

namespace IISDefensiveAI.Agent;

/// <summary>
/// Thin wrapper around IIS <see cref="ServerManager"/> for application pool inspection and recycle,
/// plus local JSON feedback for false-positive suppression and baseline refinement.
/// </summary>
public class IISController
{
    public const string FalsePositivesFileName = "false_positives.json";

    public static readonly TimeSpan RecycleCooldown = TimeSpan.FromMinutes(15);

    /// <summary>How many synthetic samples to inject per safe label during nightly baseline training.</summary>
    public const int BaselineAugmentationRepeatsPerSafeLabel = 5;

    private static readonly JsonSerializerOptions FeedbackJsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions FeedbackJsonWriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRecycleUtcByPool =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _recycleGate = new();

    private readonly object _feedbackLock = new();
    private long _feedbackFileWriteTicks;
    private List<SafeFeedbackEntry> _safeFeedbackEntries = new();

    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<IISController> _logger;

    /// <summary>UTC time of the last successful <see cref="RecycleAppPool"/> on this process.</summary>
    public DateTimeOffset? LastSuccessfulRecycleUtc { get; private set; }

    /// <summary>Pool name passed to the last successful recycle.</summary>
    public string? LastRecycledPoolName { get; private set; }

    public IISController(IHostEnvironment hostEnvironment, ILogger<IISController> logger)
    {
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    private string FalsePositivesPath => Path.Combine(_hostEnvironment.ContentRootPath, FalsePositivesFileName);

    /// <summary>Records a path/latency pair as benign (false positive) for anomaly suppression and nightly baseline augmentation.</summary>
    public void MarkAsSafe(string requestPath, double latency)
    {
        var normalized = RequestPathNormalizer.Normalize(requestPath);

        lock (_feedbackLock)
        {
            RefreshSafeFeedbackFromDisk_NoLock();

            _safeFeedbackEntries.Add(new SafeFeedbackEntry
            {
                RequestPath = normalized,
                LatencyMs = latency,
                ToleranceMs = 10,
                MarkedUtc = DateTimeOffset.UtcNow,
            });

            SaveSafeFeedback_NoLock();
            TouchFeedbackCacheTicks_NoLock();
        }

        _logger.LogInformation(
            "Marked pattern as safe: path {RequestPath}, latency {LatencyMs} ms (file {File}).",
            normalized,
            latency,
            FalsePositivesPath);
    }

    /// <summary>True if a user-marked safe pattern matches this path and latency (within tolerance).</summary>
    public bool IsMarkedSafe(string requestPath, double latencyMs)
    {
        lock (_feedbackLock)
        {
            RefreshSafeFeedbackFromDisk_NoLock();
            var key = RequestPathNormalizer.Normalize(requestPath);

            foreach (var e in _safeFeedbackEntries)
            {
                if (!string.Equals(e.RequestPath, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tol = e.ToleranceMs > 0 ? e.ToleranceMs : 10;
                if (Math.Abs(latencyMs - e.LatencyMs) <= tol)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Synthetic log rows derived from safe feedback so <see cref="NightlyLearningService"/> can fold them into per-path baselines.
    /// </summary>
    public IReadOnlyList<LogEntry> GetSafeFeedbackAugmentationForBaseline(int repeatsPerLabel = BaselineAugmentationRepeatsPerSafeLabel)
    {
        lock (_feedbackLock)
        {
            RefreshSafeFeedbackFromDisk_NoLock();
            if (_safeFeedbackEntries.Count == 0)
                return Array.Empty<LogEntry>();

            var list = new List<LogEntry>(_safeFeedbackEntries.Count * Math.Max(1, repeatsPerLabel));
            foreach (var e in _safeFeedbackEntries)
            {
                for (var i = 0; i < Math.Max(1, repeatsPerLabel); i++)
                    list.Add(CreateAugmentationLogEntry(e));
            }

            return list;
        }
    }

    private void RefreshSafeFeedbackFromDisk_NoLock()
    {
        var path = FalsePositivesPath;
        if (!File.Exists(path))
        {
            _safeFeedbackEntries = new List<SafeFeedbackEntry>();
            _feedbackFileWriteTicks = 0;
            return;
        }

        var ticks = File.GetLastWriteTimeUtc(path).Ticks;
        if (ticks == _feedbackFileWriteTicks)
            return;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var doc = JsonSerializer.Deserialize<FalsePositivesFeedbackFile>(json, FeedbackJsonReadOptions);
            _safeFeedbackEntries = doc?.Entries is { Count: > 0 }
                ? new List<SafeFeedbackEntry>(doc.Entries)
                : new List<SafeFeedbackEntry>();
            _feedbackFileWriteTicks = ticks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read safe-feedback file {Path}; using empty set.", path);
            _safeFeedbackEntries = new List<SafeFeedbackEntry>();
        }
    }

    private void SaveSafeFeedback_NoLock()
    {
        var path = FalsePositivesPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var doc = new FalsePositivesFeedbackFile { Entries = _safeFeedbackEntries };
        var json = JsonSerializer.Serialize(doc, FeedbackJsonWriteOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private void TouchFeedbackCacheTicks_NoLock()
    {
        if (File.Exists(FalsePositivesPath))
            _feedbackFileWriteTicks = File.GetLastWriteTimeUtc(FalsePositivesPath).Ticks;
    }

    private static LogEntry CreateAugmentationLogEntry(SafeFeedbackEntry e) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = "Information",
        MessageTemplate = "[SafeFeedback] Baseline augmentation sample",
        Properties = new LogEntry.LogProperties
        {
            RequestPath = e.RequestPath,
            ElapsedMilliseconds = e.LatencyMs,
        },
    };

    /// <summary>Enumerates all IIS application pools and their current state.</summary>
    public List<AppPoolStatus> GetAppPoolStatuses()
    {
        if (!OperatingSystem.IsWindows())
            return new List<AppPoolStatus> { new("(IIS)", "Unavailable") };

        try
        {
            using var serverManager = new ServerManager();
            var list = new List<AppPoolStatus>(serverManager.ApplicationPools.Count);
            foreach (var pool in serverManager.ApplicationPools)
                list.Add(new AppPoolStatus(pool.Name, pool.State.ToString()));

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate IIS application pools.");
            return new List<AppPoolStatus> { new("(IIS)", $"Error:{ex.GetType().Name}") };
        }
    }

    /// <summary>
    /// Resolves the IIS application pool for a URL request path by longest-prefix match against each site's virtual applications.
    /// </summary>
    public string? GetAppPoolNameForPath(string requestPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (string.IsNullOrWhiteSpace(requestPath) || string.Equals(requestPath, "(unknown)", StringComparison.OrdinalIgnoreCase))
            return null;

        var normalizedRequest = NormalizeIisVirtualPath(requestPath);

        try
        {
            using var serverManager = new ServerManager();
            string? bestPool = null;
            var bestLen = -1;

            foreach (var site in serverManager.Sites)
            {
                foreach (var app in site.Applications)
                {
                    var appPath = NormalizeIisVirtualPath(app.Path);
                    if (!VirtualPathMatchesRequest(normalizedRequest, appPath))
                        continue;

                    var pool = app.ApplicationPoolName?.Trim();
                    if (string.IsNullOrEmpty(pool))
                        continue;

                    if (appPath.Length > bestLen)
                    {
                        bestLen = appPath.Length;
                        bestPool = pool;
                    }
                }
            }

            if (bestPool is null)
                _logger.LogDebug("No IIS application matched request path {RequestPath} (normalized: {Normalized}).", requestPath, normalizedRequest);

            return bestPool;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve application pool for request path {RequestPath}", requestPath);
            return null;
        }
    }

    private static string NormalizeIisVirtualPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var s = path.Trim().Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal))
            s = s.Replace("//", "/", StringComparison.Ordinal);

        if (!s.StartsWith('/'))
            s = "/" + s;

        while (s.Length > 1 && s.EndsWith('/'))
            s = s[..^1];

        return s.Length == 0 ? "/" : s;
    }

    private static bool VirtualPathMatchesRequest(string normalizedRequest, string normalizedAppPath)
    {
        if (string.Equals(normalizedRequest, normalizedAppPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedAppPath == "/")
            return normalizedRequest.Length > 0;

        return normalizedRequest.StartsWith(normalizedAppPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns a human-readable status for the pool, or an error/not-found description.</summary>
    public string GetAppPoolStatus(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName))
            return "InvalidPoolName";

        if (!OperatingSystem.IsWindows())
            return "UnavailableNonWindows";

        try
        {
            using var serverManager = new ServerManager();
            var pool = serverManager.ApplicationPools[poolName];
            if (pool is null)
                return $"NotFound:{poolName}";

            return pool.State.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read app pool status for {PoolName}", poolName);
            return $"Error:{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Recycles the application pool. Enforces at most one successful recycle per pool per <see cref="RecycleCooldown"/> to avoid recycle loops.
    /// </summary>
    /// <returns><see langword="true"/> if <c>Recycle()</c> was invoked successfully.</returns>
    public bool RecycleAppPool(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            _logger.LogWarning("RecycleAppPool skipped: pool name is empty.");
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("RecycleAppPool skipped: not running on Windows.");
            return false;
        }

        lock (_recycleGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastRecycleUtcByPool.TryGetValue(poolName, out var last) && now - last < RecycleCooldown)
            {
                var wait = RecycleCooldown - (now - last);
                _logger.LogWarning(
                    "RecycleAppPool skipped for {PoolName}: cooldown active ({RemainingMinutes:F1} min remaining of {CooldownMinutes} min).",
                    poolName,
                    wait.TotalMinutes,
                    RecycleCooldown.TotalMinutes);
                return false;
            }

            try
            {
                using var serverManager = new ServerManager();
                var pool = serverManager.ApplicationPools[poolName];
                if (pool is null)
                {
                    _logger.LogWarning("RecycleAppPool: application pool {PoolName} was not found.", poolName);
                    return false;
                }

                pool.Recycle();
                var recycledAt = DateTimeOffset.UtcNow;
                _lastRecycleUtcByPool[poolName] = recycledAt;
                LastSuccessfulRecycleUtc = recycledAt;
                LastRecycledPoolName = poolName;
                _logger.LogWarning("Application pool {PoolName} recycle was requested successfully.", poolName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recycle app pool {PoolName}", poolName);
                return false;
            }
        }
    }
}
