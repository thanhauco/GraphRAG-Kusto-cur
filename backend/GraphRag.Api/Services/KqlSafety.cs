namespace GraphRag.Api.Services;

public static class KqlSafety
{
    /// <summary>Demo mode requires both a row cap (take/limit) and a time bound.</summary>
    public static bool LooksBounded(string kql)
    {
        var lower = kql.ToLowerInvariant();
        var padded = $" {lower} ";
        var hasTake =
            padded.Contains(" take ", StringComparison.Ordinal)
            || padded.Contains(" limit ", StringComparison.Ordinal)
            || lower.Contains("| take ", StringComparison.Ordinal)
            || lower.Contains("| limit ", StringComparison.Ordinal);

        var hasTime =
            lower.Contains("ago(", StringComparison.Ordinal)
            || lower.Contains("between(", StringComparison.Ordinal)
            || lower.Contains("now(", StringComparison.Ordinal)
            || lower.Contains("datetime(", StringComparison.Ordinal);

        return hasTake && hasTime;
    }
}
