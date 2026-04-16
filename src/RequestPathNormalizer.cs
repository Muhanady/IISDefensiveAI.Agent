namespace IISDefensiveAI.Agent;

/// <summary>
/// Canonical request path keys for grouping (matches monitoring/baseline behavior; collapses trailing slashes).
/// </summary>
public static class RequestPathNormalizer
{
    public static string Normalize(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return "(unknown)";

        var s = requestPath.Trim();
        while (s.Length > 1 && s.EndsWith('/'))
            s = s[..^1];

        return s;
    }
}
