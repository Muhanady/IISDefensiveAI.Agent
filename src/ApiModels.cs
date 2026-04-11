namespace IISDefensiveAI.Agent;

public sealed class AgentStatusResponse
{
    public string? AppPoolName { get; init; }

    public string AppPoolStatus { get; init; } = string.Empty;

    public IReadOnlyList<AnomalyRecord> RecentAnomalies { get; init; } = Array.Empty<AnomalyRecord>();
}

public sealed class MarkSafeRequest
{
    public string? RequestPath { get; set; }

    public double LatencyMs { get; set; }
}
