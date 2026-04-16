using System.Text.Json.Serialization;

namespace IISDefensiveAI.Agent;

public sealed class ErrorDetail
{
    public string ErrorType { get; init; } = string.Empty;

    public int Count { get; init; }
}

public sealed class ApiAnalyticsResponse
{
    public string RequestPath { get; init; } = string.Empty;

    public int CallCount { get; init; }

    public int ErrorCount { get; init; }

    /// <summary>Distinct error keys (exception first line or message template) and how often each occurred for this path.</summary>
    public IReadOnlyList<ErrorDetail> ErrorBreakdown { get; init; } = [];

    /// <summary>Calls per hour: <c>CallCount / max(GlobalDensityHours, 0.01)</c>; derived only from log entry timestamps.</summary>
    public double AverageCallsPerHour { get; init; }

    public double AverageElapsedMs { get; init; }

    public double MaxElapsedMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("sourceFile")]
    public string? SourceFile { get; init; }
}

/// <summary>Root payload for <c>GET /analytics</c> and JSON snapshot files (metadata + filtered stats).</summary>
public sealed class AnalyticsReport
{
    /// <summary>UTC timestamp of the earliest log line included in aggregation (when any).</summary>
    public DateTime? LogSampleWindowStartUtc { get; init; }

    /// <summary>UTC timestamp of the latest log line included in aggregation (when any).</summary>
    public DateTime? LogSampleWindowEndUtc { get; init; }

    /// <summary>Hours between first and last log entry timestamps (UTC); rounded to 2 decimals in analytics exports and API payloads.</summary>
    public double GlobalDensityHours { get; init; }

    public string FilterApplied { get; init; } = string.Empty;

    public IReadOnlyList<ApiAnalyticsResponse> Stats { get; init; } = Array.Empty<ApiAnalyticsResponse>();
}
