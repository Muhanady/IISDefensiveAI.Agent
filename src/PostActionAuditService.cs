using System.Text.Json;
using IISDefensiveAI.Agent.Models;

namespace IISDefensiveAI.Agent;

/// <summary>
/// After an app pool recycle, watches the same <see cref="LogEntry.LogProperties.RequestPath"/> for 5 minutes
/// to judge whether latency returned to the baseline band (or improved materially without baseline).
/// </summary>
public sealed class PostActionAuditService : IDisposable
{
    public static readonly TimeSpan AuditDuration = TimeSpan.FromMinutes(5);

    public const string OutcomesFileName = "remediation_outcomes.jsonl";

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = false };

    private static readonly object OutcomesFileLock = new();

    private readonly object _gate = new();
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<PostActionAuditService> _logger;
    private Timer? _expiryTimer;
    private AuditSession? _session;

    public PostActionAuditService(IHostEnvironment hostEnvironment, ILogger<PostActionAuditService> logger)
    {
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    private string OutcomesPath => Path.Combine(_hostEnvironment.ContentRootPath, OutcomesFileName);

    /// <summary>
    /// Starts audit mode after a successful recycle. Replaces any prior active audit (prior audit is closed as superseded).
    /// </summary>
    public void BeginAfterSuccessfulRecycle(
        LogEntry triggerEntry,
        PathBaselineStats? baselineForPath,
        double? triggerLatencyMs,
        string appPoolName,
        string? poolStatusBeforeRecycle)
    {
        var path = NormalizeRequestPath(triggerEntry.Properties?.RequestPath);
        var auditId = Guid.NewGuid();
        var endsAt = DateTimeOffset.UtcNow.Add(AuditDuration);

        AuditSession? superseded = null;
        lock (_gate)
        {
            if (_session is { Completed: false })
                superseded = DetachSessionUnsafe();

            _expiryTimer?.Dispose();
            _session = new AuditSession
            {
                AuditId = auditId,
                RequestPath = path,
                BaselineForPath = baselineForPath,
                TriggerLatencyMs = triggerLatencyMs,
                AppPoolName = appPoolName,
                PoolStatusBeforeRecycle = poolStatusBeforeRecycle,
                TriggerMessageTemplate = triggerEntry.MessageTemplate,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndsAtUtc = endsAt,
            };

            _expiryTimer = new Timer(
                _ => OnAuditWindowExpired(),
                null,
                AuditDuration,
                Timeout.InfiniteTimeSpan);
        }

        if (superseded is not null)
            FinalizeDetachedSession(superseded, ineffective: true, "superseded_by_new_recycle", resolvedLatency: null);

        _logger.LogWarning(
            "Post-Action Audit Mode started for path {RequestPath} until {EndsAt:o} (recycle of pool {PoolName}).",
            path,
            endsAt,
            appPoolName);
    }

    /// <summary>Evaluate log lines while an audit is active (same request path).</summary>
    public void TryEvaluateLogEntry(LogEntry entry)
    {
        AuditSession? detached = null;
        var ineffective = false;
        var reason = "";
        double? resolved = null;

        lock (_gate)
        {
            if (_session is null || _session.Completed)
                return;

            if (DateTimeOffset.UtcNow > _session.EndsAtUtc)
            {
                detached = DetachSessionUnsafe();
                ineffective = true;
                reason = "timer_expired_latency_not_recovered";
            }
            else if (!string.Equals(NormalizeRequestPath(entry.Properties?.RequestPath), _session.RequestPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            else if (LogEntryDiagnostics.IndicatesSqlTimeout(entry))
            {
                _session.ErrorLikeEventsDuringAudit++;
                detached = DetachSessionUnsafe();
                ineffective = true;
                reason = "sql_timeout_during_audit";
            }
            else if (entry.Properties?.ElapsedMilliseconds is { } ms && double.IsFinite(ms))
            {
                if (IsLatencyRecovered(ms, _session))
                {
                    detached = DetachSessionUnsafe();
                    ineffective = false;
                    reason = "baseline_recovered";
                    resolved = ms;
                }
            }
        }

        if (detached is not null)
            FinalizeDetachedSession(detached, ineffective, reason, resolved);
    }

    private void OnAuditWindowExpired()
    {
        AuditSession? detached = null;
        lock (_gate)
        {
            if (_session is null || _session.Completed)
                return;

            detached = DetachSessionUnsafe();
        }

        if (detached is not null)
            FinalizeDetachedSession(detached, ineffective: true, "timer_expired_latency_not_recovered", resolvedLatency: null);
    }

    private AuditSession DetachSessionUnsafe()
    {
        var s = _session!;
        _session = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        s.Completed = true;
        return s;
    }

    private static bool IsLatencyRecovered(double elapsedMs, AuditSession session)
    {
        var stats = session.BaselineForPath;
        if (stats is { SampleCount: >= 2, StdDevMs: > 0 })
        {
            var low = stats.MeanMs - 2 * stats.StdDevMs;
            var high = stats.MeanMs + 2 * stats.StdDevMs;
            return elapsedMs >= low && elapsedMs <= high;
        }

        if (session.TriggerLatencyMs is > 0 && elapsedMs <= session.TriggerLatencyMs.Value * 0.7)
            return true;

        return false;
    }

    private void FinalizeDetachedSession(AuditSession snap, bool ineffective, string reason, double? resolvedLatency)
    {
        if (ineffective)
        {
            _logger.LogError("Action Ineffective - Escalating.");
            _logger.LogDebug(
                "Post-action audit ended: path {RequestPath}, reason {Reason}, auditId {AuditId}.",
                snap.RequestPath,
                reason,
                snap.AuditId);
        }
        else
        {
            _logger.LogWarning("Action Effective.");
            _logger.LogDebug(
                "Post-action audit ended: path {RequestPath}, resolvedLatencyMs {LatencyMs}, auditId {AuditId}.",
                snap.RequestPath,
                resolvedLatency,
                snap.AuditId);
        }

        var outcome = ineffective ? "ineffective_escalating" : "effective";
        var record = new RemediationOutcomeLogRecord
        {
            AuditId = snap.AuditId,
            StartedAtUtc = snap.StartedAtUtc,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Action = "app_pool_recycle",
            AppPoolName = snap.AppPoolName,
            RequestPath = snap.RequestPath,
            TriggerLatencyMs = snap.TriggerLatencyMs,
            Outcome = outcome,
            Reason = reason,
            ResolvedLatencyMs = resolvedLatency,
            ErrorLikeEventsDuringAudit = snap.ErrorLikeEventsDuringAudit,
            TriggerMessageTemplate = snap.TriggerMessageTemplate,
            PoolStatusBeforeRecycle = snap.PoolStatusBeforeRecycle,
        };

        AppendOutcomeRecord(record);
    }

    private void AppendOutcomeRecord(RemediationOutcomeLogRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(OutcomesPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(record, JsonWriteOptions) + Environment.NewLine;
            lock (OutcomesFileLock)
                File.AppendAllText(OutcomesPath, line);

            _logger.LogInformation("Recorded remediation outcome to {Path} (audit {AuditId}, outcome {Outcome}).", OutcomesPath, record.AuditId, record.Outcome);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append remediation outcome for audit {AuditId}.", record.AuditId);
        }
    }

    private static string NormalizeRequestPath(string? requestPath) =>
        RequestPathNormalizer.Normalize(requestPath);

    public void Dispose()
    {
        AuditSession? orphaned = null;
        lock (_gate)
        {
            if (_session is { Completed: false })
                orphaned = DetachSessionUnsafe();
            else
            {
                _expiryTimer?.Dispose();
                _expiryTimer = null;
                _session = null;
            }
        }

        if (orphaned is not null)
            FinalizeDetachedSession(orphaned, ineffective: true, "host_shutdown", resolvedLatency: null);
    }

    private sealed class AuditSession
    {
        public Guid AuditId { get; init; }

        public string RequestPath { get; init; } = string.Empty;

        public PathBaselineStats? BaselineForPath { get; init; }

        public double? TriggerLatencyMs { get; init; }

        public string AppPoolName { get; init; } = string.Empty;

        public string? PoolStatusBeforeRecycle { get; init; }

        public string? TriggerMessageTemplate { get; init; }

        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset EndsAtUtc { get; init; }

        public int ErrorLikeEventsDuringAudit { get; set; }

        public bool Completed { get; set; }
    }
}
