using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class TailcatRelayClassifierTests
{
    private static readonly TailcatRelayClassifierOptions Options = new(
        MeowSshRelayHosts: ["derp1.meowssh.app", "derp2.meowssh.app"],
        MeowSshDerpMapUrls: ["https://relay.meowssh.app/derpmap.json"],
        PublicDefaultRelayHosts: ["tc302a.ipn.dev", "tc302b.ipn.dev"]);

    [Fact]
    public void EmbeddedMeowSshHostsClassifyAsMeowSsh()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(0, "DERP1.MEOWSSH.APP.", "derp2.meowssh.app"),
            null,
            Options);

        Assert.Equal(TailcatRelayClass.MeowSsh, result.RelayClass);
        Assert.Equal(["derp1.meowssh.app", "derp2.meowssh.app"], result.RelayHosts);
    }

    [Fact]
    public void ShortAddressUsingMeowSshMapClassifiesAsMeowSsh()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(regionId: 410),
            "https://relay.meowssh.app/derpmap.json",
            Options);

        Assert.Equal(TailcatRelayClass.MeowSsh, result.RelayClass);
    }

    [Fact]
    public void ShortAddressUsingDefaultMapClassifiesAsPublicDefault()
    {
        var result = TailcatRelayClassifier.Classify(Details(regionId: 302), null, Options);

        Assert.Equal(TailcatRelayClass.PublicDefault, result.RelayClass);
    }

    [Fact]
    public void EmbeddedKnownPublicHostsClassifyAsPublicDefault()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(302, "tc302a.ipn.dev", "TC302B.IPN.DEV."),
            TailcatRelayClassifierOptions.TailcatDefaultDerpMapUrl,
            Options);

        Assert.Equal(TailcatRelayClass.PublicDefault, result.RelayClass);
    }

    [Fact]
    public void ExplicitThirdPartyMapClassifiesAsUserOwned()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(77, "derp.example.net"),
            "https://example.net/derp.json",
            Options);

        Assert.Equal(TailcatRelayClass.UserOwned, result.RelayClass);
    }

    [Fact]
    public void SelfContainedUnknownRelayWithoutProvenanceStaysUnknown()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(77, "derp.example.net"),
            null,
            Options);

        Assert.Equal(TailcatRelayClass.Unknown, result.RelayClass);
    }

    [Fact]
    public void MixedOwnedAndUnknownHostsFailClosed()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(410, "derp1.meowssh.app", "evil.example"),
            "https://relay.meowssh.app/derpmap.json",
            Options);

        Assert.Equal(TailcatRelayClass.Unknown, result.RelayClass);
    }

    [Fact]
    public void TrustedMapWithMismatchedEmbeddedHostsFailsClosed()
    {
        var result = TailcatRelayClassifier.Classify(
            Details(410, "evil.example"),
            "https://relay.meowssh.app/derpmap.json",
            Options);

        Assert.Equal(TailcatRelayClass.Unknown, result.RelayClass);
    }

    private static TailcatAddressDetails Details(long regionId, params string[] hosts) => new(
        ResolvedAddress: "tc-test",
        ServerPublicKey: "nodekey:server",
        ServerDiscoPublicKey: null,
        HasPresharedKey: false,
        RegionId: regionId,
        EmbeddedRegions: hosts.Length == 0
            ? []
            :
            [
                new TailcatDerpRegionDetails(
                    regionId,
                    Code: null,
                    Name: null,
                    hosts.Select(host => new TailcatDerpNodeDetails(
                        Name: null,
                        HostName: host,
                        CertName: null,
                        IPv4: null,
                        IPv6: null,
                        StunPort: 0,
                        DerpPort: 443)).ToArray())
            ]);
}
