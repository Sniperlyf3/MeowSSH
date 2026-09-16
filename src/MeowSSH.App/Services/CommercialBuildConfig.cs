using System.Reflection;

namespace MeowSSH.App.Services;

/// <summary>
/// Commercial capabilities that must not become sellable merely because a
/// product happens to be active in Google Play Console.
/// </summary>
internal static class CommercialBuildConfig
{
    private const string ProCloudSalesEnabledKey = "MeowSSH.Commercial.ProCloudSalesEnabled";

    public static bool ProCloudSalesEnabled =>
        bool.TryParse(GetMetadata(ProCloudSalesEnabledKey), out var enabled) && enabled;

    private static string GetMetadata(string key) =>
        typeof(CommercialBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;
}
