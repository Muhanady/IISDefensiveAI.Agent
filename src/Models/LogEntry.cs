using System.Text.Json.Serialization;

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
        [JsonPropertyName("ElapsedMilliseconds")]
        public double? ElapsedMilliseconds { get; set; }

        /// <summary>Some sinks emit <c>Elapsed</c> instead of <c>ElapsedMilliseconds</c>; same backing value.</summary>
        [JsonPropertyName("Elapsed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        public double? Elapsed
        {
            get => ElapsedMilliseconds;
            set => ElapsedMilliseconds = value;
        }

        public int? StatusCode { get; set; }

        public string? SourceContext { get; set; }

        public string? RequestId { get; set; }

        /// <summary>HTTP request path when present in Serilog/ASP.NET structured logs.</summary>
        public string? RequestPath { get; set; }
    }
}
