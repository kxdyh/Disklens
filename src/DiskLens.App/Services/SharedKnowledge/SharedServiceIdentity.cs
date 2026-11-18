namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>Stable local identity for the two official addresses of the same service.</summary>
internal static class SharedServiceIdentity
{
    internal const string DefaultEndpoint = "https://api.disklens.site/";
    private const string LegacyEndpoint = "https://disklens-shared-beta-v11.disklens-shared-knowledge-beta.workers.dev/";

    internal static bool CanRestoreFrom(string? savedEndpoint, Uri currentEndpoint)
        => string.Equals(savedEndpoint, currentEndpoint.AbsoluteUri, StringComparison.Ordinal)
            || Uri.TryCreate(savedEndpoint, UriKind.Absolute, out var saved)
                && GetStorageIdentity(saved) == LegacyEndpoint
                && GetStorageIdentity(currentEndpoint) == LegacyEndpoint;

    internal static string GetStorageIdentity(Uri endpoint)
    {
        // Only these HTTPS root origins share the existing installation, consent,
        // outbox and encrypted observation map. Overrides remain service-isolated.
        if (endpoint.IsAbsoluteUri && endpoint.Scheme == Uri.UriSchemeHttps
            && endpoint.IsDefaultPort && endpoint.AbsolutePath == "/"
            && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query)
            && string.IsNullOrEmpty(endpoint.Fragment)
            && (endpoint.Host.Equals("api.disklens.site", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.Equals("disklens-shared-beta-v11.disklens-shared-knowledge-beta.workers.dev", StringComparison.OrdinalIgnoreCase)))
            return LegacyEndpoint;
        return endpoint.AbsoluteUri;
    }
}
