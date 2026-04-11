using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IISDefensiveAI.Agent.Models;
using Microsoft.Extensions.Options;

namespace IISDefensiveAI.Agent;

/// <summary>
/// Calls a local Ollama instance for root-cause style explanations of log anomalies.
/// </summary>
public class DiagnosticReasoningService
{
    private static readonly JsonSerializerOptions OllamaJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DiagnosticReasoningOptions _options;
    private readonly ILogger<DiagnosticReasoningService> _logger;

    public DiagnosticReasoningService(
        HttpClient http,
        IOptions<DiagnosticReasoningOptions> options,
        ILogger<DiagnosticReasoningService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds a prompt from the log entry and returns the model's text response (or an error summary if the call fails).
    /// </summary>
    public async Task<string> GetRootCauseAnalysisAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        var exceptionText = string.IsNullOrWhiteSpace(entry.Exception) ? "(none)" : entry.Exception;
        var messageTemplate = string.IsNullOrWhiteSpace(entry.MessageTemplate) ? "(none)" : entry.MessageTemplate;
        var elapsed = entry.Properties?.ElapsedMilliseconds;
        var requestPath = entry.Properties?.RequestPath;

        var userPrompt =
            "As an IIS expert, analyze this .NET exception and latency spike. What is the likely root cause and the immediate fix?\n\n" +
            $"MessageTemplate: {messageTemplate}\n" +
            $"Exception:\n{exceptionText}\n" +
            $"ElapsedMilliseconds: {elapsed?.ToString() ?? "n/a"}\n" +
            $"RequestPath: {requestPath ?? "n/a"}\n";

        var request = new OllamaGenerateRequest
        {
            Model = _options.Model,
            Prompt = userPrompt,
            Stream = false,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("api/generate", request, OllamaJsonOptions, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            OllamaGenerateResponse? body = null;
            try
            {
                body = JsonSerializer.Deserialize<OllamaGenerateResponse>(raw, OllamaJsonOptions);
            }
            catch (JsonException)
            {
                // Body may be plain text on some error paths.
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = body?.Error ?? raw;
                _logger.LogWarning("Ollama returned {Status}: {Body}", (int)response.StatusCode, err);
                return $"[Ollama HTTP {(int)response.StatusCode}] {err}";
            }

            if (!string.IsNullOrEmpty(body?.Error))
            {
                _logger.LogWarning("Ollama error field: {Error}", body.Error);
                return $"[Ollama error] {body.Error}";
            }

            return string.IsNullOrWhiteSpace(body?.Response)
                ? "[Ollama returned an empty response]"
                : body.Response.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama root-cause request failed.");
            return $"[Ollama unavailable: {ex.GetType().Name}: {ex.Message}]";
        }
    }

    /// <summary>Appends a timestamped RCA block to <c>diagnostics_rca.log</c> under <paramref name="contentRoot"/>.</summary>
    public async Task AppendRcaToDiagnosticsLogAsync(LogEntry entry, string contentRoot, CancellationToken cancellationToken = default)
    {
        var analysis = await GetRootCauseAnalysisAsync(entry, cancellationToken);
        var path = Path.Combine(contentRoot, "diagnostics_rca.log");
        var block =
            $"========== {DateTimeOffset.UtcNow:O} (UTC) ==========\n" +
            analysis +
            "\n\n";

        await File.AppendAllTextAsync(path, block, cancellationToken);
        _logger.LogInformation("Wrote diagnostic RCA entry to {Path}.", path);
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
