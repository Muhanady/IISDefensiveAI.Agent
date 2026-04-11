using IISDefensiveAI.Agent.Models;

namespace IISDefensiveAI.Agent;

/// <summary>Ring buffer of recent IID spike detections for the HTTP status API.</summary>
public class AnomalyTelemetry
{
    private const int MaxEntries = 5;

    private readonly object _gate = new();
    private readonly List<AnomalyRecord> _items = new();

    public void Record(LogEntry entry, string normalizedRequestPath, double elapsedMs, bool suppressedBySafeFeedback)
    {
        lock (_gate)
        {
            _items.Add(new AnomalyRecord
            {
                DetectedAtUtc = DateTimeOffset.UtcNow,
                RequestPath = normalizedRequestPath,
                ElapsedMs = elapsedMs,
                MessageTemplate = entry.MessageTemplate,
                Level = entry.Level,
                SuppressedBySafeFeedback = suppressedBySafeFeedback,
            });

            while (_items.Count > MaxEntries)
                _items.RemoveAt(0);
        }
    }

    public IReadOnlyList<AnomalyRecord> GetRecent()
    {
        lock (_gate)
            return _items.ToArray();
    }
}

public sealed class AnomalyRecord
{
    public DateTimeOffset DetectedAtUtc { get; init; }

    public string RequestPath { get; init; } = string.Empty;

    public double ElapsedMs { get; init; }

    public string? MessageTemplate { get; init; }

    public string? Level { get; init; }

    public bool SuppressedBySafeFeedback { get; init; }
}
