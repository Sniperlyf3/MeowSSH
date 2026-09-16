using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class EntitlementTailcatHubServiceTests
{
    [Fact]
    public async Task FreeTierCannotStartPhoneServer()
    {
        var inner = new FakeHub();
        var service = new EntitlementTailcatHubService(inner, new FakeEntitlements(EntitlementTier.Free));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartServerAsync(ServerRequest(exitNode: false)));

        Assert.Equal(0, inner.ServerStarts);
    }

    [Fact]
    public async Task ProTierCanStartPhoneServerAndExitNode()
    {
        var inner = new FakeHub();
        var service = new EntitlementTailcatHubService(inner, new FakeEntitlements(EntitlementTier.Pro));

        await service.StartServerAsync(ServerRequest(exitNode: true));

        Assert.Equal(1, inner.ServerStarts);
    }

    [Fact]
    public async Task FreeTierCannotStartUdpForwardButTcpRemainsAvailable()
    {
        var inner = new FakeHub();
        var service = new EntitlementTailcatHubService(inner, new FakeEntitlements(EntitlementTier.Free));

        await service.StartForwardAsync(new TailcatForwardRequest("tc-test", ["8080:80"], Udp: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartForwardAsync(new TailcatForwardRequest("tc-test", ["5353:5353"], Udp: true)));

        Assert.Equal(1, inner.ForwardStarts);
    }

    [Fact]
    public async Task AdvancedServeRangesRequirePro()
    {
        var inner = new FakeHub();
        var service = new EntitlementTailcatHubService(inner, new SelectiveEntitlements(
            PremiumFeature.TailcatPhoneServer));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartServerAsync(ServerRequest(exitNode: false) with { ServeTargets = ["8000-8010"] }));

        Assert.Equal(0, inner.ServerStarts);
    }

    [Fact]
    public async Task StopOperationsRemainAvailableAfterEntitlementExpires()
    {
        var inner = new FakeHub();
        var entitlements = new FakeEntitlements(EntitlementTier.Pro);
        var service = new EntitlementTailcatHubService(inner, entitlements);

        await service.StartServerAsync(ServerRequest(exitNode: true));
        var forward = await service.StartForwardAsync(new TailcatForwardRequest("tc-test", ["5353:5353"], Udp: true));
        entitlements.Tier = EntitlementTier.Free;

        await service.StopForwardAsync(forward.Id);
        await service.StopServerAsync();

        Assert.Equal(1, inner.ForwardStops);
        Assert.Equal(1, inner.ServerStops);
    }

    private static TailcatServeRequest ServerRequest(bool exitNode) => new(
        TimeSpan.FromHours(1),
        "nodekey:test-client",
        EnableShell: false,
        AuthorizedSshKeys: null,
        EnableFiles: false,
        SharedFolder: null,
        FileMode: "ro",
        EnableExitNode: exitNode);

    private sealed class FakeEntitlements(EntitlementTier tier) : IEntitlementService
    {
        public EntitlementTier Tier { get; set; } = tier;
        public EntitlementSnapshot Current => new(
            Tier,
            Tier == EntitlementTier.Free ? EntitlementSource.None : EntitlementSource.ServerVerifiedGooglePlay,
            DateTimeOffset.UtcNow,
            Tier == EntitlementTier.Free ? null : DateTimeOffset.UtcNow.AddHours(1));

        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => EntitlementPolicy.Allows(Current, feature, DateTimeOffset.UtcNow);
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class SelectiveEntitlements(params PremiumFeature[] allowed) : IEntitlementService
    {
        private readonly HashSet<PremiumFeature> _allowed = [.. allowed];
        public EntitlementSnapshot Current { get; } = new(EntitlementTier.Pro, EntitlementSource.ServerVerifiedGooglePlay, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        public event EventHandler? Changed { add { } remove { } }
        public bool Has(PremiumFeature feature) => _allowed.Contains(feature);
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class FakeHub : ITailcatHubService
    {
        private readonly List<TailcatForwardSnapshot> _forwards = [];
        private TailcatServerSnapshot? _server;
        public int ServerStarts { get; private set; }
        public int ServerStops { get; private set; }
        public int ForwardStarts { get; private set; }
        public int ForwardStops { get; private set; }

        public TailcatHubSnapshot Snapshot => new(_server, null, [.. _forwards], []);
        public event EventHandler? Changed;

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServerStarts++;
            _server = new TailcatServerSnapshot("tc-test", DateTimeOffset.UtcNow.AddHours(1), request.EnableShell, request.EnableFiles,
                request.EnableExitNode, request.AllowAnyClient, request.UseTailcatCredentialForShell, request.SharedFolder, request.FileMode,
                request.ServeTargets ?? []);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StopServerAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServerStops++;
            _server = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardStarts++;
            var snapshot = new TailcatForwardSnapshot(Guid.NewGuid(), request.Address, request.Mappings, ["127.0.0.1:18080"], request.ClientKey, request.Udp);
            _forwards.Add(snapshot);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(snapshot);
        }

        public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardStops++;
            _forwards.RemoveAll(item => item.Id == id);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopSocksAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) => Task.FromResult("nodekey:test");
        public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => Task.FromResult(address);
        public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TailcatRemoteFile>>([]);
        public Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
