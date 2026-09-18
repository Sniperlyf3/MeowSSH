using System.Reflection;
using MeowSSH.Core.Licensing;

namespace MeowSSH.App.Services;

/// <summary>
/// Public Tailcat networking configuration baked into the release. Keeping the
/// DERP map explicit here prevents a native dependency upgrade from silently
/// changing which relay infrastructure a commercial build uses.
/// </summary>
internal static class TailcatBuildConfig
{
    private const string DerpMapUrlKey = "MeowSSH.Tailcat.DerpMapUrl";

    public static string DerpMapUrl => ManagedDerpMapUrl.Resolve(
        GetMetadata(DerpMapUrlKey),
        LicensingBuildConfig.ApiBaseUrl);

    private static string GetMetadata(string key) =>
        typeof(TailcatBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;
}
