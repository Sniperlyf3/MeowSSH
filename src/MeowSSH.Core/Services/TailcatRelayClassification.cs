namespace MeowSSH.Core.Services;

public enum TailcatRelayClass
{
    Unknown = 0,
    MeowSsh = 1,
    UserOwned = 2,
    PublicDefault = 3,
}

public sealed record TailcatRelayClassification(
    TailcatRelayClass RelayClass,
    IReadOnlyList<string> RelayHosts,
    long RegionId,
    string? DerpMapUrl);

/// <summary>
/// Explicit trust configuration for relay classification. MeowSSH-owned infrastructure is
/// recognized only by exact normalized host/map values supplied by trusted app/backend config;
/// never by a substring or DNS suffix heuristic.
/// </summary>
public sealed record TailcatRelayClassifierOptions(
    IReadOnlyCollection<string> MeowSshRelayHosts,
    IReadOnlyCollection<string>? MeowSshDerpMapUrls = null,
    IReadOnlyCollection<string>? PublicDefaultRelayHosts = null,
    IReadOnlyCollection<string>? PublicDefaultDerpMapUrls = null)
{
    public const string TailcatDefaultDerpMapUrl = "https://tailcat.dev/derpmap.json";
}

public static class TailcatRelayClassifier
{
    public static TailcatRelayClassification Classify(
        TailcatAddressDetails address,
        string? selectedDerpMapUrl,
        TailcatRelayClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(options);

        var hosts = address.EmbeddedRegions
            .SelectMany(static region => region.Nodes)
            .Select(static node => NormalizeHost(node.HostName))
            .Where(static host => host is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static host => host, StringComparer.Ordinal)
            .ToArray();

        var meowHosts = NormalizeHosts(options.MeowSshRelayHosts);
        var publicHosts = NormalizeHosts(options.PublicDefaultRelayHosts ?? []);
        var map = NormalizeMapUrl(selectedDerpMapUrl);
        var meowMaps = NormalizeMapUrls(options.MeowSshDerpMapUrls ?? []);
        var publicMaps = NormalizeMapUrls(
            (options.PublicDefaultDerpMapUrls ?? [])
                .Append(TailcatRelayClassifierOptions.TailcatDefaultDerpMapUrl));

        var mapIsMeow = map is not null && meowMaps.Contains(map);
        var explicitPublicMap = map is not null && publicMaps.Contains(map);
        var mapIsPublic = map is null || explicitPublicMap;

        if (hosts.Length > 0)
        {
            var allMeow = hosts.All(meowHosts.Contains);
            var allPublic = hosts.All(publicHosts.Contains);
            var anyMeow = hosts.Any(meowHosts.Contains);
            var anyPublic = hosts.Any(publicHosts.Contains);

            if (allMeow)
                return Result(TailcatRelayClass.MeowSsh);

            // If the caller explicitly resolved through the known public map, that map provenance
            // is authoritative unless the embedded result conflicts with a known MeowSSH-owned host.
            // This lets freshly expanded public-default addresses remain identifiable even when the
            // upstream relay hostname set changes, without treating arbitrary self-contained imports
            // as public by default.
            if (explicitPublicMap && !anyMeow)
                return Result(TailcatRelayClass.PublicDefault);

            if (allPublic)
                return Result(TailcatRelayClass.PublicDefault);

            // MeowSSH ownership stays strict: a trusted MeowSSH map that embeds any unrecognized
            // host is ambiguous and must never become billable merely because the map URL matched.
            if (mapIsMeow || anyMeow || anyPublic)
                return Result(TailcatRelayClass.Unknown);

            // Embedded third-party hosts are only called user-owned when the caller explicitly
            // selected a non-default DERP map. A self-contained address with no provenance stays Unknown.
            if (map is not null && !mapIsPublic)
                return Result(TailcatRelayClass.UserOwned);

            return Result(TailcatRelayClass.Unknown);
        }

        if (mapIsMeow)
            return Result(TailcatRelayClass.MeowSsh);

        if (address.RegionId != 0 && mapIsPublic)
            return Result(TailcatRelayClass.PublicDefault);

        if (map is not null && !mapIsPublic)
            return Result(TailcatRelayClass.UserOwned);

        return Result(TailcatRelayClass.Unknown);

        TailcatRelayClassification Result(TailcatRelayClass relayClass) =>
            new(relayClass, hosts, address.RegionId, selectedDerpMapUrl);
    }

    private static HashSet<string> NormalizeHosts(IEnumerable<string> hosts) =>
        hosts.Select(NormalizeHost)
            .Where(static host => host is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> NormalizeMapUrls(IEnumerable<string> urls) =>
        urls.Select(NormalizeMapUrl)
            .Where(static url => url is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string? NormalizeMapUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return value.Trim();

        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
