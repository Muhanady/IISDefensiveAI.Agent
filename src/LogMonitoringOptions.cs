namespace IISDefensiveAI.Agent;

public class LogMonitoringOptions
{
    public const string SectionName = "LogMonitoring";

    /// <summary>Directory containing IIS/Serilog JSON log files (absolute or relative to content root).</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>File name filter for <see cref="FileSystemWatcher"/> (e.g. *.log, *.json).</summary>
    public string FileFilter { get; set; } = "*.json";

    /// <summary>
    /// Allow-list of application pool names eligible for automatic recycle after a critical anomaly.
    /// The pool is resolved from the log request path via IIS site/application mapping; empty list disables recycle.
    /// </summary>
    public List<string> AuthorizedAppPools { get; set; } = new();

    /// <summary>How far back to look for SQL timeout signals when evaluating a critical anomaly.</summary>
    public int SqlTimeoutLookbackMinutes { get; set; } = 10;

    /// <summary>Minimum SQL-timeout log signals required in the lookback window for an anomaly to be treated as critical.</summary>
    public int SqlTimeoutsRequiredForCritical { get; set; } = 2;

    /// <summary>Capacity for the per-path elapsed-millisecond ring buffer.</summary>
    public int ElapsedMsBufferCapacity { get; set; } = 100;

    /// <summary>Number of recent p-value samples retained for spike detection history.</summary>
    public int SpikePValueHistoryLength { get; set; } = 35;

    /// <summary>When true, critical anomalies trigger real-time Ollama root-cause analysis.</summary>
    public bool EnableAutoRca { get; set; } = true;

    /// <summary>Optional directory for RCA / diagnostic log output; when null or empty, output uses the app content root.</summary>
    public string? DiagnosisDirectory { get; set; }
}
