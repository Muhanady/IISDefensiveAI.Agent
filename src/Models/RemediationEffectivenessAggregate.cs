namespace IISDefensiveAI.Agent.Models;

/// <summary>Written by <see cref="NightlyLearningService"/> from <c>remediation_outcomes.jsonl</c>.</summary>
public sealed class RemediationEffectivenessAggregate
{
    public DateTimeOffset GeneratedAtUtc { get; set; }

    public int TotalOutcomesRead { get; set; }

    /// <summary>Key = normalized request path.</summary>
    public Dictionary<string, PathRemediationStats> ByRequestPath { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PathRemediationStats
{
    public int RecycleEffective { get; set; }

    public int RecycleIneffectiveEscalating { get; set; }
}
