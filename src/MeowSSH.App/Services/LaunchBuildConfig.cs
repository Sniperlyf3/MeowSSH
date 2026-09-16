using System.Reflection;
using MeowSSH.UI.Services;

namespace MeowSSH.App.Services;

internal static class LaunchBuildConfig
{
    private const string PrivacyPolicyUrlKey = "MeowSSH.Launch.PrivacyPolicyUrl";
    private const string SupportUrlKey = "MeowSSH.Launch.SupportUrl";
    private const string TermsOfServiceUrlKey = "MeowSSH.Launch.TermsOfServiceUrl";

    public static AppExternalLinks ExternalLinks { get; } = new(
        HttpsUriOrNull(GetMetadata(PrivacyPolicyUrlKey)),
        HttpsUriOrNull(GetMetadata(SupportUrlKey)),
        HttpsUriOrNull(GetMetadata(TermsOfServiceUrlKey)));

    private static Uri? HttpsUriOrNull(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    private static string GetMetadata(string key) =>
        typeof(LaunchBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;
}
