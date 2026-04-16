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

    /// <summary>Calls per hour over the global log time span (first to last processed entry); 0 if span is zero.</summary>
    public double AverageCallsPerHour { get; init; }

    public double AverageElapsedMs { get; init; }

    public double MaxElapsedMs { get; init; }
}
