namespace IISDefensiveAI.Agent.Models;

public class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }

    public string? Level { get; set; }

    public string? MessageTemplate { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public string? Exception { get; set; }

    public LogProperties? Properties { get; set; }

    public class LogProperties
    {
        public double? ElapsedMilliseconds { get; set; }

        public int? StatusCode { get; set; }

        public string? SourceContext { get; set; }

        public string? RequestId { get; set; }

        /// <summary>HTTP request path when present in Serilog/ASP.NET structured logs.</summary>
        public string? RequestPath { get; set; }
    }
}
