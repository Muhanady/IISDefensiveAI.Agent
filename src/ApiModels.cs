namespace IISDefensiveAI.Agent;

public sealed record AppPoolStatus(string Name, string Status);

public sealed class AgentStatusResponse
{
    /// <summary>Configured single pool for anomaly reaction, when set.</summary>
    public string? AppPoolName { get; init; }

    /// <summary>Status string for <see cref="AppPoolName"/>; or <c>all_pools</c> when monitoring every pool.</summary>
    public string AppPoolStatus { get; init; } = string.Empty;

    /// <summary>One entry when a specific pool is configured; all pools when in watch-all mode.</summary>
    public List<AppPoolStatus> AppPools { get; init; } = new();

    public IReadOnlyList<AnomalyRecord> RecentAnomalies { get; init; } = Array.Empty<AnomalyRecord>();
}

public sealed class MarkSafeRequest
{
    public string? RequestPath { get; set; }

    public double LatencyMs { get; set; }
}
