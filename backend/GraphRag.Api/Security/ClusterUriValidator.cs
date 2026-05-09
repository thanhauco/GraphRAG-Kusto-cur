namespace GraphRag.Api.Security;

public static class ClusterUriValidator
{
    public static void EnsureAllowed(string clusterUri, IReadOnlyList<string> allowlist)
    {
        if (string.IsNullOrWhiteSpace(clusterUri))
            throw new ArgumentException("Cluster URI is required.", nameof(clusterUri));

        var uri = clusterUri.Trim();
        Uri? parsed;
        try
        {
            parsed = new Uri(uri, UriKind.Absolute);
        }
        catch
        {
            throw new InvalidOperationException("Invalid cluster URI format.");
        }

        if (string.IsNullOrEmpty(parsed.Host))
            throw new InvalidOperationException("Invalid cluster URI (missing host).");

        var normalized = uri.TrimEnd('/').ToLowerInvariant();
        var allowed = allowlist.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().TrimEnd('/').ToLowerInvariant()).ToList();
        if (allowed.Count == 0)
            throw new InvalidOperationException("Server allowlist is empty.");

        foreach (var prefix in allowed)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException("Cluster URI is not allowed by server allowlist.");
    }
}
