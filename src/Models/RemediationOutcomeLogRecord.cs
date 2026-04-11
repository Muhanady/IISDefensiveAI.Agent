namespace IISDefensiveAI.Agent.Models;

/// <summary>One line in <c>remediation_outcomes.jsonl</c> for nightly learning and ops review.</summary>
public sealed class RemediationOutcomeLogRecord
{
    public Guid AuditId { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset EndedAtUtc { get; init; }

    public string Action { get; init; } = "app_pool_recycle";

    public string AppPoolName { get; init; } = string.Empty;

    public string RequestPath { get; init; } = string.Empty;

    public double? TriggerLatencyMs { get; init; }

    /// <summary>effective | ineffective_escalating</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>e.g. baseline_recovered, timer_expired, sql_timeout_during_audit</summary>
    public string Reason { get; init; } = string.Empty;

    public double? ResolvedLatencyMs { get; init; }

    public int ErrorLikeEventsDuringAudit { get; init; }

    public string? TriggerMessageTemplate { get; init; }

    public string? PoolStatusBeforeRecycle { get; init; }
}
