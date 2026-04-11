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

    private static readonly JsonSerializerOptions PropertiesPromptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions LogLineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly HttpClient _http;
    private readonly DiagnosticReasoningOptions _options;
    private readonly LogMonitoringOptions _logMonitoringOptions;
    private readonly ILogger<DiagnosticReasoningService> _logger;

    public DiagnosticReasoningService(
        HttpClient http,
        IOptions<DiagnosticReasoningOptions> options,
        IOptions<LogMonitoringOptions> logMonitoringOptions,
        ILogger<DiagnosticReasoningService> logger)
    {
        _http = http;
        _options = options.Value;
        _logMonitoringOptions = logMonitoringOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds a prompt from the log entry and returns the model's text response (or an error summary if the call fails).
    /// </summary>
    /// <param name="occurrenceCount">When greater than 1, the prompt describes an aggregated recurring pattern.</param>
    public async Task<string> GetRootCauseAnalysisAsync(
        LogEntry entry,
        int occurrenceCount = 1,
        CancellationToken cancellationToken = default)
    {
        var exceptionText = string.IsNullOrWhiteSpace(entry.Exception) ? "(none)" : entry.Exception;
        var messageTemplate = string.IsNullOrWhiteSpace(entry.MessageTemplate) ? "(none)" : entry.MessageTemplate;
        var requestPath = entry.Properties?.RequestPath ?? "n/a";

        var serializedProperties = SerializeLoggedPropertiesForPrompt(entry.Properties);

        var userPrompt = $"""
            You are a Senior .NET Architect. You are analyzing a recurring error pattern in a high-load IIS environment.

            [STATISTICS]
            - Frequency: This specific error occurred {occurrenceCount} times in the last log file.

            [REPRESENTATIVE SAMPLE]
            - Request Path: {requestPath}
            - Message Template: {messageTemplate}
            - Logged Properties (JSON): {serializedProperties}
            - Full Exception: {exceptionText}

            [INSTRUCTIONS]
            1. Based on the frequency and the data, provide a 'Pattern Analysis'.
            2. Deep-dive into the 'Properties' (e.g., look for RDMPSM-TRXN006 or session conflicts).
            3. Provide one 'Definitive Root Cause' and one 'Scalable Fix' that would resolve all {occurrenceCount} instances.
            """;

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

    /// <summary>
    /// Appends a timestamped RCA block to <paramref name="outputFileName"/> under
    /// <see cref="LogMonitoringOptions.DiagnosisDirectory"/> when set, otherwise <paramref name="contentRoot"/>.
    /// </summary>
    public async Task AppendRcaToDiagnosticsLogAsync(
        LogEntry entry,
        string contentRoot,
        string outputFileName,
        CancellationToken cancellationToken = default,
        int occurrenceCount = 1)
    {
        var analysis = await GetRootCauseAnalysisAsync(entry, occurrenceCount, cancellationToken);
        var path = GetDiagnosisOutputPath(contentRoot, outputFileName);
        var block =
            $"========== {DateTimeOffset.UtcNow:O} (UTC) ==========\n" +
            $"This error occurred {occurrenceCount} times.\n\n" +
            analysis +
            "\n\n";

        await File.AppendAllTextAsync(path, block, cancellationToken);
        _logger.LogInformation("Wrote diagnostic RCA entry to {Path}.", path);
    }

    /// <summary>
    /// Serializes <see cref="LogEntry.Properties"/> including <see cref="LogEntry.LogProperties.ExtensionData"/>
    /// so nested payloads (e.g. <c>exceptionResponseList</c> with kiosk/session business errors) appear in the prompt.
    /// </summary>
    private static string SerializeLoggedPropertiesForPrompt(LogEntry.LogProperties? properties)
    {
        if (properties is null)
            return "(none)";

        return JsonSerializer.Serialize(properties, PropertiesPromptJsonOptions);
    }

    private static string BuildErrorSignatureKey(LogEntry entry)
    {
        var messageTemplate = entry.MessageTemplate ?? string.Empty;
        var exceptionType = ExtractExceptionTypeSignature(entry.Exception);
        var statusCode = entry.Properties?.StatusCode?.ToString() ?? string.Empty;
        return string.Join('\u001e', messageTemplate, exceptionType, statusCode);
    }

    private static string ExtractExceptionTypeSignature(string? exception)
    {
        if (string.IsNullOrWhiteSpace(exception))
            return "(none)";

        var span = exception.AsSpan().TrimStart();
        var nl = span.IndexOfAny("\r\n");
        if (nl >= 0)
            span = span[..nl];

        var line = span.ToString().Trim();
        var colon = line.IndexOf(':');
        return colon > 0 ? line[..colon].Trim() : line;
    }

    private string GetDiagnosisOutputPath(string contentRoot, string outputFileName)
    {
        var diagnosisRoot = ResolveDiagnosisDirectoryRoot(contentRoot);
        Directory.CreateDirectory(diagnosisRoot);
        return Path.Combine(diagnosisRoot, outputFileName);
    }

    private string ResolveDiagnosisDirectoryRoot(string contentRoot)
    {
        var configured = _logMonitoringOptions.DiagnosisDirectory?.Trim();
        if (string.IsNullOrEmpty(configured))
            return contentRoot;

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(contentRoot, configured));
    }

    /// <summary>
    /// Scans log files under <paramref name="directory"/> matching <paramref name="filter"/>,
    /// writing RCA for each source file to <c>{stem}_diagnostics.log</c> under the configured diagnosis directory (or <paramref name="contentRoot"/>).
    /// Each qualifying entry uses a dedicated <see cref="CancellationTokenSource"/> timeout from <see cref="DiagnosticReasoningOptions.RequestTimeoutSeconds"/>.
    /// </summary>
    /// <returns>The total number of source log lines grouped into at least one RCA (same as sum of per-signature counts).</returns>
    public async Task<int> AnalyzeLogFolderAsync(string directory, string filter, string contentRoot, CancellationToken ct)
    {
        var logFiles = Directory.GetFiles(directory, filter);
        _logger.LogInformation("Starting bulk analysis of {Count} files in {Directory}", logFiles.Length, directory);
        var analyzedLineTotal = 0;

        foreach (var file in logFiles)
        {
            var fileName = Path.GetFileName(file);
            var outputFileName = $"{Path.GetFileNameWithoutExtension(file)}_diagnostics.log";
            _logger.LogInformation("Starting analysis for {FileName}. Output: {OutputName}", fileName, outputFileName);

            try
            {
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(file, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read log file {File}. Skipping.", file);
                    continue;
                }

                var errorGroups = new Dictionary<string, (LogEntry Sample, int Count)>();

                foreach (var line in lines)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<LogEntry>(line.Trim('\uFEFF'), LogLineJsonOptions);
                        if (entry?.Level != "Error" && string.IsNullOrEmpty(entry?.Exception))
                            continue;

                        var key = BuildErrorSignatureKey(entry!);
                        if (errorGroups.TryGetValue(key, out var bucket))
                            errorGroups[key] = (bucket.Sample, bucket.Count + 1);
                        else
                            errorGroups[key] = (entry!, 1);
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }

                if (errorGroups.Count == 0)
                    continue;

                var outPath = GetDiagnosisOutputPath(contentRoot, outputFileName);
                await File.WriteAllTextAsync(outPath, string.Empty, ct);

                foreach (var key in errorGroups.Keys.Order(StringComparer.Ordinal))
                {
                    var (sample, count) = errorGroups[key];
                    try
                    {
                        var mt = sample.MessageTemplate;
                        var preview = string.IsNullOrEmpty(mt) ? key[..Math.Min(40, key.Length)] : mt[..Math.Min(40, mt.Length)];
                        _logger.LogInformation(
                            "AI is analyzing aggregated error ({Occurrences}x): {Preview}... (This may take a minute)",
                            count,
                            preview);

                        using var entryCts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

                        await AppendRcaToDiagnosticsLogAsync(
                            sample,
                            contentRoot,
                            outputFileName,
                            entryCts.Token,
                            count);

                        analyzedLineTotal += count;
                        _logger.LogInformation(
                            "RCA complete for signature. Progress: {LinesCovered} error lines covered across files.",
                            analyzedLineTotal);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning(
                            "RCA timed out or was cancelled for a signature in {File}. Continuing batch.",
                            fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Signature RCA failed in {File}. Continuing batch.", fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected failure processing file {File}. Continuing to next file.", fileName);
            }
        }

        _logger.LogInformation("Bulk analysis finished. Total error lines aggregated into RCA: {Count}", analyzedLineTotal);
        return analyzedLineTotal;
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
