using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class TailcatWorkspaceServiceTests
{
    [Fact]
    public async Task FreeTierCannotSaveOrStartWorkspace()
    {
        var store = new MemoryTailcatWorkspaceStore();
        var workspace = Workspace();
        await store.SaveAsync(workspace);
        var service = new TailcatWorkspaceService(store, new FakeHub(), new MutableEntitlements(EntitlementTier.Free));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(workspace));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(workspace.Id));
    }

    [Fact]
    public async Task ProWorkspaceStartsAndStopRemainsAvailableAfterEntitlementExpires()
    {
        var store = new MemoryTailcatWorkspaceStore();
        var workspace = Workspace();
        await store.SaveAsync(workspace);
        var hub = new FakeHub();
        var entitlements = new MutableEntitlements(EntitlementTier.Pro);
        var service = new TailcatWorkspaceService(store, hub, entitlements);

        await service.StartAsync(workspace.Id);

        Assert.NotNull(service.Active);
        Assert.NotNull(hub.Snapshot.Socks);
        Assert.Single(hub.Snapshot.Forwards);
        Assert.True(service.Active!.OwnsSocks);

        entitlements.Tier = EntitlementTier.Free;
        await service.StopAsync();

        Assert.Null(service.Active);
        Assert.Null(hub.Snapshot.Socks);
        Assert.Empty(hub.Snapshot.Forwards);
    }

    [Fact]
    public async Task FailedActivationRollsBackResourcesStartedByWorkspace()
    {
        var store = new MemoryTailcatWorkspaceStore();
        var workspace = Workspace() with
        {
            Forwards =
            [
                new TailcatWorkspaceForward(["8080:80"]),
                new TailcatWorkspaceForward(["8443:443"]),
            ],
        };
        await store.SaveAsync(workspace);
        var hub = new FakeHub { FailOnForwardNumber = 2 };
        var service = new TailcatWorkspaceService(store, hub, new MutableEntitlements(EntitlementTier.Pro));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(workspace.Id));

        Assert.Null(service.Active);
        Assert.Null(hub.Snapshot.Socks);
        Assert.Empty(hub.Snapshot.Forwards);
    }

    [Fact]
    public async Task WorkspaceDoesNotStopSocksItDidNotStart()
    {
        var store = new MemoryTailcatWorkspaceStore();
        var workspace = Workspace();
        await store.SaveAsync(workspace);
        var hub = new FakeHub();
        await hub.StartSocksAsync(new TailcatSocksRequest("127.0.0.1:9999", "preexisting"));
        var service = new TailcatWorkspaceService(store, hub, new MutableEntitlements(EntitlementTier.Pro));

        await service.StartAsync(workspace.Id);
        Assert.False(service.Active!.OwnsSocks);

        await service.StopAsync();
        Assert.NotNull(hub.Snapshot.Socks);
    }

    private static TailcatWorkspace Workspace() => new(
        Guid.NewGuid(),
        "Home lab",
        "tc-home",
        "client-default",
        null,
        StartSocks: true,
        SocksListenAddress: "127.0.0.1:0",
        Forwards: [new TailcatWorkspaceForward(["8080:80"])]);

    private sealed class MutableEntitlements(EntitlementTier tier) : IEntitlementService
    {
        public EntitlementTier Tier { get; set; } = tier;

        public EntitlementSnapshot Current => new(
            Tier,
            Tier == EntitlementTier.Free ? EntitlementSource.None : EntitlementSource.ServerVerifiedGooglePlay,
            DateTimeOffset.UtcNow,
            Tier == EntitlementTier.Free ? null : DateTimeOffset.UtcNow.AddDays(1));

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => EntitlementPolicy.Allows(Current, feature, DateTimeOffset.UtcNow);
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class FakeHub : ITailcatHubService
    {
        private readonly List<TailcatForwardSnapshot> _forwards = [];
        private TailcatSocksSnapshot? _socks;
        private int _forwardStarts;

        public int? FailOnForwardNumber { get; init; }
        public TailcatHubSnapshot Snapshot => new(null, _socks, [.. _forwards], []);
        public event EventHandler? Changed;

        public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _socks = new TailcatSocksSnapshot(request.ListenAddress, request.ClientKey);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StopSocksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _socks = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _forwardStarts++;
            if (FailOnForwardNumber == _forwardStarts)
                throw new InvalidOperationException("Synthetic forward failure.");

            var snapshot = new TailcatForwardSnapshot(
                Guid.NewGuid(), request.Address, request.Mappings, ["127.0.0.1:18080"], request.ClientKey, request.Udp);
            _forwards.Add(snapshot);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(snapshot);
        }

        public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _forwards.RemoveAll(item => item.Id == id);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopServerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
