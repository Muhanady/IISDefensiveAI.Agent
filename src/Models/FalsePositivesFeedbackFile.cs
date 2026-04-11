namespace IISDefensiveAI.Agent.Models;

/// <summary>Local file (<c>false_positives.json</c>) storing user-marked "safe" latency patterns.</summary>
public class FalsePositivesFeedbackFile
{
    public List<SafeFeedbackEntry> Entries { get; set; } = new();
}

public class SafeFeedbackEntry
{
    public string RequestPath { get; set; } = string.Empty;

    public double LatencyMs { get; set; }

    /// <summary>Half-width band around <see cref="LatencyMs"/> for matching (ms).</summary>
    public double ToleranceMs { get; set; } = 10;

    public DateTimeOffset MarkedUtc { get; set; }
}
