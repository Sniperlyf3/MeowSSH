using System.Text.Json;

namespace MeowSSH.Core.Services;

/// <summary>Which relay a Tailcat address can be reached through.</summary>
public enum TailcatRoute
{
    /// <summary>MeowSSH's managed relay: used without asking.</summary>
    Managed,

    /// <summary>A relay the user chose themselves, or one the address carries: used without asking.</summary>
    Custom,

    /// <summary>The public tailcat.dev relay: used only with the user's consent.</summary>
    Public,
}

/// <param name="Route">Which relay the address is reached through.</param>
/// <param name="DerpMapUrl">
/// The relay map to dial with. Always explicit for <see cref="TailcatRoute.Managed"/> and
/// <see cref="TailcatRoute.Public"/>, because the app-wide default is the managed map and a
/// public address resolved against it would silently fail; null for an address that carries
/// its own relay.
/// </param>
public sealed record TailcatRouteDecision(TailcatRoute Route, string? DerpMapUrl);

/// <summary>What the app knows about which relays are whose.</summary>
/// <param name="ManagedRegionIds">Region ids in MeowSSH's managed relay map.</param>
/// <param name="ManagedRelayHosts">DERP host names in MeowSSH's managed relay map.</param>
/// <param name="PublicRelayHosts">DERP host names in the public tailcat.dev map.</param>
/// <param name="ManagedMapKnown">False when the managed map could not be read.</param>
/// <param name="PublicMapKnown">False when the public map could not be read.</param>
public sealed record TailcatRelayDirectory(
    IReadOnlySet<long> ManagedRegionIds,
    IReadOnlySet<string> ManagedRelayHosts,
    IReadOnlySet<string> PublicRelayHosts,
    bool ManagedMapKnown = true,
    bool PublicMapKnown = true);

public interface ITailcatRelayDirectorySource
{
    Task<TailcatRelayDirectory> GetAsync(CancellationToken cancellationToken = default);
}

/// <param name="ManagedDerpMapUrl">The build's managed relay map, or null when the build has none.</param>
public sealed record TailcatRelayPolicyOptions(string? ManagedDerpMapUrl)
{
    public const string PublicDerpMapUrl = TailcatRelayClassifierOptions.TailcatDefaultDerpMapUrl;
}

/// <summary>
/// Decides how a Tailcat address is dialled. The rules, from the product
/// owner: use MeowSSH's relay wherever it works; a relay the user picked is
/// theirs to use; the public relay only ever with the user's say-so, never as
/// a silent fallback. Every unclear case lands on the side that asks.
/// </summary>
public sealed class TailcatRelayPolicy(
    ITailcatHubService hub,
    ITailcatRelayDirectorySource directory,
    TailcatRelayPolicyOptions options)
{
    public string? ManagedDerpMapUrl => options.ManagedDerpMapUrl;

    /// <param name="address">The Tailcat address to reach.</param>
    /// <param name="userDerpMapUrl">A relay map the user entered for this host or operation.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<TailcatRouteDecision> DecideAsync(
        string address,
        string? userDerpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (!string.IsNullOrWhiteSpace(userDerpMapUrl))
        {
            // The public map typed in by hand is still the public relay.
            return IsPublicMap(userDerpMapUrl)
                ? new(TailcatRoute.Public, TailcatRelayPolicyOptions.PublicDerpMapUrl)
                : new(TailcatRoute.Custom, userDerpMapUrl.Trim());
        }

        var known = await directory.GetAsync(cancellationToken).ConfigureAwait(false);
        // A build with no managed relay has nothing to route "managed" to.
        var managedAvailable = options.ManagedDerpMapUrl is not null;
        var details = await hub.InspectAddressAsync(address.Trim(), cancellationToken: cancellationToken).ConfigureAwait(false);
        var hosts = details.EmbeddedRegions
            .SelectMany(static region => region.Nodes)
            .Select(static node => NormalizeHost(node.HostName))
            .Where(static host => host is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (hosts.Length > 0)
        {
            if (managedAvailable && hosts.All(known.ManagedRelayHosts.Contains)) return Managed();
            // A full public address names the public relay's own hosts. When
            // the public map could not be read, an unrecognised relay might be
            // it, so ask rather than use it silently.
            if (!known.PublicMapKnown || hosts.All(known.PublicRelayHosts.Contains) || hosts.Any(known.PublicRelayHosts.Contains))
                return Public();
            return new(TailcatRoute.Custom, null);
        }

        if (managedAvailable && known.ManagedRegionIds.Contains(details.RegionId)) return Managed();
        // Our map could not be read: try our relay and fail clearly, never
        // fall back to the public one.
        if (managedAvailable && !known.ManagedMapKnown) return Managed();
        return Public();
    }

    private TailcatRouteDecision Managed() => new(TailcatRoute.Managed, options.ManagedDerpMapUrl);

    private static TailcatRouteDecision Public() => new(TailcatRoute.Public, TailcatRelayPolicyOptions.PublicDerpMapUrl);

    private static bool IsPublicMap(string url) =>
        string.Equals(url.Trim().TrimEnd('/'), TailcatRelayPolicyOptions.PublicDerpMapUrl, StringComparison.OrdinalIgnoreCase);

    internal static string? NormalizeHost(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : host.Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>
/// Reads both relay maps over HTTPS (they are public JSON, not relay
/// traffic) and remembers them for an hour, or a minute after a failure.
/// </summary>
public sealed class HttpTailcatRelayDirectorySource(
    HttpClient http,
    TailcatRelayPolicyOptions options,
    TimeProvider? timeProvider = null) : ITailcatRelayDirectorySource, IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TailcatRelayDirectory? _cached;
    private DateTimeOffset _expires;

    public async Task<TailcatRelayDirectory> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _time.GetUtcNow() < _expires) return _cached;

            var managed = options.ManagedDerpMapUrl is null ? Map.Empty : await ReadAsync(options.ManagedDerpMapUrl, cancellationToken).ConfigureAwait(false);
            var publicMap = await ReadAsync(TailcatRelayPolicyOptions.PublicDerpMapUrl, cancellationToken).ConfigureAwait(false);
            _cached = new TailcatRelayDirectory(managed.Regions, managed.Hosts, publicMap.Hosts, managed.Known, publicMap.Known);
            _expires = _time.GetUtcNow() + (managed.Known && publicMap.Known ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1));
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Map> ReadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = await http.GetStreamAsync(url, timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return Map.Parse(document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
            && !cancellationToken.IsCancellationRequested)
        {
            return Map.Unknown;
        }
    }

    /// <summary>Tailscale's DERPMap JSON: {"Regions": {"901": {"RegionID": 901, "Nodes": [{"HostName": ...}]}}}.</summary>
    internal sealed record Map(IReadOnlySet<long> Regions, IReadOnlySet<string> Hosts, bool Known)
    {
        public static readonly Map Empty = new(new HashSet<long>(), new HashSet<string>(), true);
        public static readonly Map Unknown = new(new HashSet<long>(), new HashSet<string>(), false);

        public static Map Parse(JsonElement root)
        {
            var regions = new HashSet<long>();
            var hosts = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(Regions), out var all) && all.ValueKind == JsonValueKind.Object)
            {
                foreach (var region in all.EnumerateObject())
                {
                    if (region.Value.TryGetProperty("RegionID", out var id) && id.TryGetInt64(out var regionId)) regions.Add(regionId);
                    else if (long.TryParse(region.Name, out var keyed)) regions.Add(keyed);
                    if (!region.Value.TryGetProperty("Nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var node in nodes.EnumerateArray())
                    {
                        if (node.TryGetProperty("HostName", out var name) && TailcatRelayPolicy.NormalizeHost(name.GetString()) is { } host)
                            hosts.Add(host);
                    }
                }
            }
            return new Map(regions, hosts, true);
        }
    }
}
