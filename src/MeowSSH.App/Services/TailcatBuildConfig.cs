using System.Reflection;

namespace MeowSSH.App.Services;

/// <summary>
/// Public Tailcat networking configuration baked into the release. Keeping the
/// DERP map explicit here prevents a native dependency upgrade from silently
/// changing which relay infrastructure a commercial build uses.
/// </summary>
internal static class TailcatBuildConfig
{
    private const string DerpMapUrlKey = "MeowSSH.Tailcat.DerpMapUrl";

    public static string DerpMapUrl => GetHttpsUrl(DerpMapUrlKey);

    private static string GetHttpsUrl(string key)
    {
        var value = typeof(TailcatBuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : string.Empty;
    }
}
