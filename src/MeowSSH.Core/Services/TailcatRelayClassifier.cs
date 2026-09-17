namespace MeowSSH.Core.Services;

/// <summary>Who owns the relay path represented by a Tailcat address.</summary>
public enum TailcatRelayClass
{
    MeowSsh,
    PublicDefault,
    UserOwned,
    Unknown,
}

/// <summary>Deployment-controlled relay classification inputs.</summary>
public sealed record TailcatRelayClassifierOptions(
    IReadOnlyCollection<string> MeowSshRelayHosts,
    IReadOnlyCollection<string> MeowSshDerpMapUrls,
    IReadOnlyCollection<string> PublicDefaultRelayHosts)
{
    // Null is the normal Tailcat-default-map case. This constant exists for
    // addresses/configurations that explicitly spell out the same provenance.
    public const string TailcatDefaultDerpMapUrl = "https://derpmap.tailcat.dev/default";
}

public sealed record TailcatRelayClassification(
    TailcatRelayClass RelayClass,
    IReadOnlyList<string> RelayHosts);

/// <summary>
/// Pure, fail-closed classification of a parsed Tailcat address. Embedded host
/// identity and DERP-map provenance must agree before an address is attributed
/// to infrastructure that MeowSSH owns or meters.
/// </summary>
public static class TailcatRelayClassifier
{
    public static TailcatRelayClassification Classify(
        TailcatAddressDetails details,
        string? derpMapUrl,
        TailcatRelayClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(options);

        var meowHosts = NormalizeHosts(options.MeowSshRelayHosts);
        var publicHosts = NormalizeHosts(options.PublicDefaultRelayHosts);
        var meowMaps = NormalizeUrls(options.MeowSshDerpMapUrls);
        var normalizedMap = NormalizeUrl(derpMapUrl);
        var usesMeowMap = normalizedMap is not null && meowMaps.Contains(normalizedMap);
        var usesPublicMap = normalizedMap is null ||
            string.Equals(
                normalizedMap,
                NormalizeUrl(TailcatRelayClassifierOptions.TailcatDefaultDerpMapUrl),
                StringComparison.OrdinalIgnoreCase);
        var usesThirdPartyMap = !usesMeowMap && !usesPublicMap;

        var relayHosts = details.EmbeddedRegions
            .SelectMany(region => region.Nodes)
            .Select(node => node.HostName)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => NormalizeHost(host!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (relayHosts.Length == 0)
        {
            var relayClass = usesMeowMap
                ? TailcatRelayClass.MeowSsh
                : usesPublicMap
                    ? TailcatRelayClass.PublicDefault
                    : TailcatRelayClass.UserOwned;
            return new(relayClass, relayHosts);
        }

        var meowCount = relayHosts.Count(meowHosts.Contains);
        var publicCount = relayHosts.Count(publicHosts.Contains);
        var unknownCount = relayHosts.Length - meowCount - publicCount;

        // Known MeowSSH hosts are only attributable to us when every embedded
        // host belongs to our fleet and provenance is not explicitly foreign.
        if (meowCount > 0)
        {
            if (meowCount == relayHosts.Length && !usesPublicMap && !usesThirdPartyMap)
                return new(TailcatRelayClass.MeowSsh, relayHosts);

            // Self-contained/full addresses may omit a map because the embedded
            // nodes are sufficient provenance.
            if (meowCount == relayHosts.Length && normalizedMap is null)
                return new(TailcatRelayClass.MeowSsh, relayHosts);

            return new(TailcatRelayClass.Unknown, relayHosts);
        }

        if (publicCount == relayHosts.Length)
        {
            if (usesMeowMap || usesThirdPartyMap)
                return new(TailcatRelayClass.Unknown, relayHosts);
            return new(TailcatRelayClass.PublicDefault, relayHosts);
        }

        // An explicit upstream/default map can legitimately introduce a relay
        // hostname newer than the app's baked public-host snapshot.
        if (unknownCount == relayHosts.Length && usesPublicMap && normalizedMap is not null)
            return new(TailcatRelayClass.PublicDefault, relayHosts);

        if (unknownCount == relayHosts.Length && usesThirdPartyMap)
            return new(TailcatRelayClass.UserOwned, relayHosts);

        // Unknown self-contained addresses and every mixed-provenance address
        // fail closed. They must not be charged to MeowSSH or silently trusted.
        return new(TailcatRelayClass.Unknown, relayHosts);
    }

    private static HashSet<string> NormalizeHosts(IEnumerable<string> hosts) =>
        hosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(NormalizeHost)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> NormalizeUrls(IEnumerable<string> urls) =>
        urls
            .Select(NormalizeUrl)
            .Where(url => url is not null)
            .Select(url => url!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url.Trim().TrimEnd('/');
    }
}
