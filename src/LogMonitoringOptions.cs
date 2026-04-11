namespace IISDefensiveAI.Agent;

public class LogMonitoringOptions
{
    public const string SectionName = "LogMonitoring";

    /// <summary>Directory containing IIS/Serilog JSON log files (absolute or relative to content root).</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>File name filter for <see cref="FileSystemWatcher"/> (e.g. *.log, *.json).</summary>
    public string FileFilter { get; set; } = "*.json";

    /// <summary>IIS application pool to inspect/recycle when a critical anomaly is detected (optional).</summary>
    public string? AnomalyReactionAppPoolName { get; set; }

    /// <summary>How far back to look for SQL timeout signals when evaluating a critical anomaly.</summary>
    public int SqlTimeoutLookbackMinutes { get; set; } = 10;

    /// <summary>Minimum SQL-timeout log signals required in the lookback window for an anomaly to be treated as critical.</summary>
    public int SqlTimeoutsRequiredForCritical { get; set; } = 2;
}
