using System.Net;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.Core.Tests.Services;

public sealed class TailcatRelayPolicyTests
{
    private const string ManagedMap = "https://api.meowssh.example/v1/derp/map";

    private static TailcatRelayDirectory Directory(bool managedKnown = true, bool publicKnown = true) => new(
        new HashSet<long> { 901 },
        new HashSet<string> { "derp1.meowssh.example" },
        new HashSet<string> { "derp1.tailcat.dev" },
        managedKnown,
        publicKnown);

    private static TailcatRelayPolicy Policy(FakeHub hub, TailcatRelayDirectory? directory = null, string? managedMap = ManagedMap) =>
        new(hub, new FixedDirectory(directory ?? Directory()), new TailcatRelayPolicyOptions(managedMap));

    [Fact]
    public async Task AnAddressInAManagedRegionUsesTheManagedMap()
    {
        var decision = await Policy(new FakeHub(region: 901)).DecideAsync("tc901");
        Assert.Equal(new TailcatRouteDecision(TailcatRoute.Managed, ManagedMap), decision);
    }

    [Fact]
    public async Task AnyOtherRegionIsThePublicRelayAndNamesThePublicMap()
    {
        // The app-wide default map is the managed one, so a public address must
        // be dialled with the public map named, or it resolves against ours.
        var decision = await Policy(new FakeHub(region: 1)).DecideAsync("tc1");
        Assert.Equal(new TailcatRouteDecision(TailcatRoute.Public, TailcatRelayPolicyOptions.PublicDerpMapUrl), decision);
    }

    [Fact]
    public async Task WhenOurMapCannotBeReadARegionOnlyAddressTriesOurRelayNeverThePublicOne()
    {
        var decision = await Policy(new FakeHub(region: 7), Directory(managedKnown: false)).DecideAsync("tc7");
        Assert.Equal(TailcatRoute.Managed, decision.Route);
    }

    [Fact]
    public async Task WithNoManagedRelayInTheBuildEveryRegionIsPublic()
    {
        var decision = await Policy(new FakeHub(region: 901), Directory(managedKnown: false), managedMap: null).DecideAsync("tc901");
        Assert.Equal(TailcatRoute.Public, decision.Route);
    }

    [Theory]
    [InlineData("DERP1.meowssh.example.", TailcatRoute.Managed)]
    [InlineData("derp1.tailcat.dev", TailcatRoute.Public)]
    [InlineData("relay.home.example", TailcatRoute.Custom)]
    public async Task AnAddressCarryingItsRelayIsClassifiedByThatRelay(string relayHost, TailcatRoute expected)
    {
        var decision = await Policy(new FakeHub(region: 0, embeddedHosts: [relayHost])).DecideAsync("tcfull");
        Assert.Equal(expected, decision.Route);
    }

    [Fact]
    public async Task AnUnrecognisedCarriedRelayAsksWhenThePublicMapCouldNotBeRead()
    {
        // It might be the public relay under a host name we have not seen.
        var decision = await Policy(new FakeHub(region: 0, embeddedHosts: ["relay.home.example"]), Directory(publicKnown: false)).DecideAsync("tcfull");
        Assert.Equal(TailcatRoute.Public, decision.Route);
    }

    [Fact]
    public async Task AMapTheUserEnteredIsTheirsUnlessItIsThePublicOne()
    {
        var hub = new FakeHub(region: 1);
        Assert.Equal(new TailcatRouteDecision(TailcatRoute.Custom, "https://relay.home.example/map"),
            await Policy(hub).DecideAsync("tc1", " https://relay.home.example/map "));
        Assert.Equal(TailcatRoute.Public, (await Policy(hub).DecideAsync("tc1", "https://TAILCAT.dev/derpmap.json/")).Route);
        Assert.Equal(0, hub.Inspections);
    }

    [Fact]
    public void TheDirectoryReadsRegionsAndHostsFromADerpMap()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"Regions": {"901": {"RegionID": 901, "Nodes": [{"HostName": "Derp1.MeowSSH.example."}]}, "902": {"Nodes": []}}}
            """);
        var map = HttpTailcatRelayDirectorySource.Map.Parse(json.RootElement);
        Assert.Equal(new HashSet<long> { 901, 902 }, map.Regions);
        Assert.Equal(new HashSet<string> { "derp1.meowssh.example" }, map.Hosts);
    }

    [Fact]
    public async Task AMapThatCannotBeFetchedIsUnknownNotEmpty()
    {
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        using var source = new HttpTailcatRelayDirectorySource(http, new TailcatRelayPolicyOptions(ManagedMap));
        var directory = await source.GetAsync();
        Assert.False(directory.ManagedMapKnown);
        Assert.False(directory.PublicMapKnown);
    }

    // --- saved hosts ---------------------------------------------------------

    private static HostRecord TailcatHost(PublicRelayConsent consent = PublicRelayConsent.Ask) => new()
    {
        Id = Guid.NewGuid(),
        Label = "phone",
        Address = "tc1",
        Transport = SshTransport.Tailcat,
        PublicRelay = consent,
    };

    [Fact]
    public async Task AManagedHostConnectsWithTheManagedMapAndNoQuestion()
    {
        var host = TailcatHost();
        var (engine, inner, prompts, _) = Engine(new FakeHub(region: 901), host);
        await engine.ConnectAsync(host, SshCredentials.None, prompts);
        Assert.Equal(ManagedMap, inner.Connected!.TailcatDerpMapUrl);
        Assert.Equal(0, prompts.Asked);
    }

    [Fact]
    public async Task ATcpHostIsNeverLookedAt()
    {
        var hub = new FakeHub(region: 1);
        var host = TailcatHost() with { Transport = SshTransport.Tcp };
        var (engine, inner, prompts, _) = Engine(hub, host);
        await engine.ConnectAsync(host, SshCredentials.None, prompts);
        Assert.Same(host, inner.Connected);
        Assert.Equal(0, hub.Inspections);
    }

    [Theory]
    [InlineData(PublicRelayChoice.ConnectOnce, PublicRelayConsent.Ask)]
    [InlineData(PublicRelayChoice.AlwaysForHost, PublicRelayConsent.Always)]
    public async Task APublicHostAsksAndKeepsOnlyAnAlways(PublicRelayChoice answer, PublicRelayConsent kept)
    {
        var host = TailcatHost();
        var (engine, inner, prompts, editor) = Engine(new FakeHub(region: 1), host);
        prompts.Answer = answer;
        await engine.ConnectAsync(host, SshCredentials.None, prompts);
        Assert.Equal(TailcatRelayPolicyOptions.PublicDerpMapUrl, inner.Connected!.TailcatDerpMapUrl);
        Assert.True(prompts.Last!.CanRemember);
        Assert.Equal(kept, editor.Saved?.PublicRelay ?? PublicRelayConsent.Ask);
    }

    [Fact]
    public async Task CancelAndNeverDoNotConnect()
    {
        var host = TailcatHost();
        var (engine, inner, prompts, _) = Engine(new FakeHub(region: 1), host);
        prompts.Answer = PublicRelayChoice.Cancel;
        Assert.Equal(SshFailure.PublicRelayDeclined, (await Assert.ThrowsAsync<SshException>(() => engine.ConnectAsync(host, SshCredentials.None, prompts))).Failure);

        var never = TailcatHost(PublicRelayConsent.Never);
        (engine, inner, prompts, _) = Engine(new FakeHub(region: 1), never);
        Assert.Equal(SshFailure.PublicRelayDeclined, (await Assert.ThrowsAsync<SshException>(() => engine.ConnectAsync(never, SshCredentials.None, prompts))).Failure);
        Assert.Equal(0, prompts.Asked);
        Assert.Null(inner.Connected);
    }

    [Fact]
    public async Task TheStoredChoiceWinsOverAStaleCopyOfTheHost()
    {
        // A session reconnecting with the host as it was before "Always".
        var stale = TailcatHost();
        var (engine, inner, prompts, _) = Engine(new FakeHub(region: 1), stale with { PublicRelay = PublicRelayConsent.Always });
        await engine.ConnectAsync(stale, SshCredentials.None, prompts);
        Assert.Equal(0, prompts.Asked);
        Assert.NotNull(inner.Connected);
    }

    [Fact]
    public async Task AnUnsavedHostIsAskedWithoutTheOptionToRemember()
    {
        var unsaved = TailcatHost();
        var (engine, _, prompts, editor) = Engine(new FakeHub(region: 1), stored: null);
        prompts.Answer = PublicRelayChoice.AlwaysForHost;
        await engine.ConnectAsync(unsaved, SshCredentials.None, prompts);
        Assert.False(prompts.Last!.CanRemember);
        Assert.Null(editor.Saved);
    }

    private static (TailcatRelaySshEngine Engine, RecordingEngine Inner, AnsweringPrompts Prompts, RecordingHosts Editor) Engine(FakeHub hub, HostRecord? stored)
    {
        var inner = new RecordingEngine();
        var hosts = new RecordingHosts(stored);
        return (new TailcatRelaySshEngine(inner, Policy(hub), hosts, hosts), inner, new AnsweringPrompts(), hosts);
    }

    // --- the hub guard -------------------------------------------------------

    [Fact]
    public async Task SharingAsksOnlyWhenItWouldSilentlyBePublic()
    {
        var prompts = new AnsweringPrompts { Answer = PublicRelayChoice.Cancel };
        var request = new TailcatServeRequest(TimeSpan.FromMinutes(5), "nodekey:a", true, null, false, null, "ro", false);

        // With a managed relay, the default share is on it: no question.
        await new PublicRelayGuardTailcatHubService(new FakeHub(region: 901), Policy(new FakeHub(region: 901)), prompts).StartServerAsync(request);
        // "Anyone", chosen on the panel, is the user's own pick.
        var noManaged = Policy(new FakeHub(region: 1), managedMap: null);
        await new PublicRelayGuardTailcatHubService(new FakeHub(region: 1), noManaged, prompts)
            .StartServerAsync(request with { DerpMapUrl = TailcatRelayPolicyOptions.PublicDerpMapUrl });
        Assert.Equal(0, prompts.Asked);

        // No managed relay and no pick: asked, and a "no" shares nothing.
        var hub = new FakeHub(region: 1);
        await Assert.ThrowsAsync<TailcatPublicRelayDeclinedException>(() => new PublicRelayGuardTailcatHubService(hub, noManaged, prompts).StartServerAsync(request));
        Assert.Null(hub.Snapshot.Server);
        Assert.Equal(1, prompts.Asked);
    }

    [Fact]
    public async Task APastedPublicAddressIsAskedEveryTimeAndDialledWithThePublicMap()
    {
        var hub = new FakeHub(region: 1);
        var prompts = new AnsweringPrompts { Answer = PublicRelayChoice.ConnectOnce };
        var guard = new PublicRelayGuardTailcatHubService(hub, Policy(hub), prompts);
        await guard.ListRemoteFilesAsync("tc1", "/");
        await guard.DownloadAsync("tc1", "/a", "/tmp/a");
        Assert.Equal(2, prompts.Asked);
        Assert.False(prompts.Last!.CanRemember);
        Assert.All(hub.DialledMaps, map => Assert.Equal(TailcatRelayPolicyOptions.PublicDerpMapUrl, map));
    }

    // --- fakes ---------------------------------------------------------------

    private sealed class FixedDirectory(TailcatRelayDirectory directory) : ITailcatRelayDirectorySource
    {
        public Task<TailcatRelayDirectory> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(directory);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8) });
    }

    private sealed class AnsweringPrompts : ISshPrompts
    {
        public PublicRelayChoice Answer { get; set; } = PublicRelayChoice.Cancel;
        public int Asked { get; private set; }
        public PublicRelayPrompt? Last { get; private set; }

        public Task<PublicRelayChoice> ConfirmPublicRelayAsync(PublicRelayPrompt prompt, CancellationToken cancellationToken = default)
        {
            Asked++;
            Last = prompt;
            return Task.FromResult(Answer);
        }

        public Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<IReadOnlyList<string>?> AnswerChallengeAsync(KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>?>(null);
    }

    private sealed class RecordingEngine : ISshEngine
    {
        public HostRecord? Connected { get; private set; }

        public Task<ISshConnection> ConnectAsync(HostRecord host, SshCredentials credentials, ISshPrompts prompts, CancellationToken cancellationToken = default)
        {
            Connected = host;
            return Task.FromResult<ISshConnection>(null!);
        }
    }

    private sealed class RecordingHosts(HostRecord? stored) : IHostDirectory, IHostEditor
    {
        public HostRecord? Saved { get; private set; }
        public event EventHandler? Changed { add { } remove { } }

        public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<HostStatus>>(stored is null ? [] : [new HostStatus(stored)]);

        public ValueTask SaveHostAsync(HostRecord host, CancellationToken cancellationToken = default)
        {
            Saved = host;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteHostAsync(Guid hostId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CredentialSummary>> GetCredentialsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask SaveCredentialAsync(CredentialRecord credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeHub(long region, IReadOnlyList<string>? embeddedHosts = null) : ITailcatHubService
    {
        private TailcatServerSnapshot? _server;

        public int Inspections { get; private set; }
        public List<string?> DialledMaps { get; } = [];
        public TailcatHubSnapshot Snapshot => new(_server, null, [], []);
        public event EventHandler? Changed { add { } remove { } }

        public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default)
        {
            Inspections++;
            IReadOnlyList<TailcatDerpRegionDetails> regions = embeddedHosts is null
                ? []
                : [new TailcatDerpRegionDetails(0, null, null, [.. embeddedHosts.Select(static h => new TailcatDerpNodeDetails(null, h, null, null, null, 0, 0))])];
            return Task.FromResult(new TailcatAddressDetails(address, "nodekey:s", null, true, region, regions));
        }

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
        {
            _server = new TailcatServerSnapshot("tcserver", DateTimeOffset.UtcNow.AddHours(1), true, false, false, false, false, null, "ro", []);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default)
        {
            DialledMaps.Add(derpMapUrl);
            return Task.FromResult<IReadOnlyList<TailcatRemoteFile>>([]);
        }

        public Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default)
        {
            DialledMaps.Add(derpMapUrl);
            return Task.CompletedTask;
        }

        public Task StopServerAsync(CancellationToken cancellationToken = default) { _server = null; return Task.CompletedTask; }
        public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopSocksAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) => Task.FromResult("nodekey:c");
        public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => Task.FromResult(address);
        public Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
