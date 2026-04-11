namespace IISDefensiveAI.Agent.Models;

/// <summary>Serialized latency baseline per request path (see <c>baseline_profile.json</c>).</summary>
public class BaselineProfile
{
    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>Mean of all <see cref="LogEntry.LogProperties.ElapsedMilliseconds"/> samples included in the profile.</summary>
    public double AverageGlobalLatencyMs { get; set; }

    public Dictionary<string, PathBaselineStats> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aggregated app-pool recycle outcomes by request path (from <c>remediation_outcomes.jsonl</c>).</summary>
    public Dictionary<string, PathRemediationStats>? RemediationEffectivenessByPath { get; set; }
}

public class PathBaselineStats
{
    public double MeanMs { get; set; }

    public double StdDevMs { get; set; }

    public double P95Ms { get; set; }

    public int SampleCount { get; set; }
}
