using IISDefensiveAI.Agent.Models;

namespace IISDefensiveAI.Agent;

/// <summary>Shared log pattern checks for monitoring and post-action audit.</summary>
public static class LogEntryDiagnostics
{
    public static bool IndicatesSqlTimeout(LogEntry entry)
    {
        var exception = entry.Exception ?? string.Empty;
        var template = entry.MessageTemplate ?? string.Empty;
        var level = entry.Level ?? string.Empty;

        if (exception.Contains("SqlException", StringComparison.OrdinalIgnoreCase))
            return true;

        if (exception.Contains("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase) &&
            exception.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        if (exception.Contains("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase) &&
            exception.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        if (exception.Contains("Timeout expired", StringComparison.OrdinalIgnoreCase) &&
            exception.Contains("SQL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (template.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
            (template.Contains("SQL", StringComparison.OrdinalIgnoreCase) ||
             template.Contains("Sql", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase) &&
            exception.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
