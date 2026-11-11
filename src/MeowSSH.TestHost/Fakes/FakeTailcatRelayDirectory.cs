using Microsoft.AspNetCore.Components;
using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Which relay <see cref="FakeTailcatHubService"/>'s addresses are on, for the
/// real <see cref="TailcatRelayPolicy"/>: MeowSSH's, unless the page was opened
/// with "publicrelay" in the query string, in which case the public one -- the
/// same query-string switch as FakeSshEngine's "relayhealth".
/// </summary>
public sealed class FakeTailcatRelayDirectory(NavigationManager navigation) : ITailcatRelayDirectorySource
{
    /// <summary>The relay host embedded in every address FakeTailcatHubService inspects.</summary>
    public const string RelayHost = "derp.example.test";

    public const string ManagedDerpMapUrl = "https://example.test/v1/derp/map";

    public Task<TailcatRelayDirectory> GetAsync(CancellationToken cancellationToken = default)
    {
        var isPublic = new Uri(navigation.Uri).Query.Contains("publicrelay", StringComparison.OrdinalIgnoreCase);
        var relay = new HashSet<string> { RelayHost };
        var none = new HashSet<string>();
        return Task.FromResult(new TailcatRelayDirectory(
            new HashSet<long>(),
            isPublic ? none : relay,
            isPublic ? relay : none));
    }
}
