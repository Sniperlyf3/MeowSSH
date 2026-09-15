using System.Reflection;

namespace MeowSSH.App.Services;

internal static class LicensingBuildConfig
{
    private const string ApiBaseUrlKey = "MeowSSH.Licensing.ApiBaseUrl";
    private const string PublicKeyKey = "MeowSSH.Licensing.PublicKeySubjectPublicKeyInfoBase64";

    // These values are public configuration, not secrets. CI injects them as
    // assembly metadata. Empty values intentionally leave licensing fail-closed.
    public static string ApiBaseUrl => GetMetadata(ApiBaseUrlKey);
    public static string PublicKeySubjectPublicKeyInfoBase64 => GetMetadata(PublicKeyKey);

    private static string GetMetadata(string key) =>
        typeof(LicensingBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;
}
