namespace IISDefensiveAI.Agent;

public class DiagnosticReasoningOptions
{
    public const string SectionName = "DiagnosticReasoning";

    /// <summary>Base URL for Ollama (no trailing path), e.g. http://localhost:11434</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name, e.g. llama3, mistral, llama3.2</summary>
    public string Model { get; set; } = "llama3";

    /// <summary>HTTP timeout for /api/generate (seconds).</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;
}
