namespace IISDefensiveAI.Agent;

public class LogAnalyticsOptions
{
    public const string SectionName = "LogAnalytics";

    /// <summary>Directory of structured JSON logs (absolute or relative to content root).</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>File pattern for <see cref="Directory.EnumerateFiles"/> (e.g. *.json).</summary>
    public string FileFilter { get; set; } = "*.json";
}
