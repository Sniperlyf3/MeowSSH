using System.Reflection;

namespace MeowSSH.App.Services;

internal static class LaunchBuildConfig
{
    private const string PrivacyPolicyUrlKey = "MeowSSH.Launch.PrivacyPolicyUrl";
    private const string SupportUrlKey = "MeowSSH.Launch.SupportUrl";

    public static string PrivacyPolicyUrl => GetMetadata(PrivacyPolicyUrlKey);
    public static string SupportUrl => GetMetadata(SupportUrlKey);

    private static string GetMetadata(string key) =>
        typeof(LaunchBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;
}
