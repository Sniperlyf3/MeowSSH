namespace MeowSSH.Core.Licensing;

/// <summary>
/// Resolves the Tailcat relay-map URL for a build. An explicit HTTPS map remains
/// available for self-hosted/distribution overrides; otherwise a build with a
/// configured MeowSSH licensing API automatically uses that control plane's
/// public managed-DERP map.
/// </summary>
public static class ManagedDerpMapUrl
{
    public const string RelativePath = "v1/derp/map";

    public static string Resolve(string? explicitMapUrl, string? licensingApiBaseUrl)
    {
        if (TryHttps(explicitMapUrl, out var explicitUri))
            return explicitUri.AbsoluteUri;

        if (!TryHttps(licensingApiBaseUrl, out var apiUri))
            return string.Empty;

        return new Uri(apiUri, RelativePath).AbsoluteUri;
    }

    private static bool TryHttps(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
