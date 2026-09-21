using MeowSSH.Core.Services;
using MeowSSH.TestHost.Fakes;
using MeowSSH.UI.Services;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Unit-level coverage for the wakelock-gap fix: TailcatPhonePanel and
/// TailcatTemporarySharePanel used to release IActiveSessionLifetime only from
/// their own Stop/Revoke click handlers, so a server that stopped
/// spontaneously (a temporary share's deadline, or the general panel's
/// server hitting the same deadline -- both SIGTERM/SIGKILL inside
/// MeowshellServer.StartDeadline) left the wakelock held with nothing left
/// for it to protect. These tests exercise the coordinator directly against a
/// fake hub, rather than through Playwright, because the thing being verified
/// -- exactly how many times IActiveSessionLifetime.Start/StopAsync were
/// called, and in what order -- has no DOM-visible signal to assert on.
/// </summary>
public sealed class TailcatServerSessionLifetimeCoordinatorTests
{
    [Fact]
    public async Task StartingTheServerAcquiresTheLifetime()
    {
        var hub = new FakeHub();
        var lifetime = new FakeActiveSessionLifetime();
        using var coordinator = new TailcatServerSessionLifetimeCoordinator(hub, lifetime);

        await hub.StartServerAsync(Request());

        Assert.Equal(1, lifetime.StartCalls);
        Assert.Equal(0, lifetime.StopCalls);
        Assert.True(lifetime.IsActive);
    }

    [Fact]
    public async Task SpontaneousExpiryReleasesTheLifetimeWithoutAnyStopBeingCalled()
    {
        // Models MeowshellServer.StartDeadline firing: the server disappears
        // via ObserveServerAsync's own Changed raise, never through
        // StopServerAsync. Before this fix, nothing subscribed to that at all.
        var hub = new FakeHub();
        var lifetime = new FakeActiveSessionLifetime();
        using var coordinator = new TailcatServerSessionLifetimeCoordinator(hub, lifetime);

        await hub.StartServerAsync(Request());
        Assert.True(lifetime.IsActive);

        await hub.SimulateDeadlineExpiryAsync();

        Assert.Equal(1, lifetime.StartCalls);
        Assert.Equal(1, lifetime.StopCalls);
        Assert.False(lifetime.IsActive);
    }

    [Fact]
    public async Task ExplicitStopReleasesExactlyOnceEvenWithTheHubsDuplicateChangedRaise()
    {
        // MeowshellTailcatHubService raises Changed twice for one explicit
        // stop: once from StopServerAsync itself, and again from
        // ObserveServerAsync's trailing raise once disposal actually
        // completes. Firing a second, state-unchanged Changed event here
        // reproduces that without needing a real subprocess.
        var hub = new FakeHub();
        var lifetime = new FakeActiveSessionLifetime();
        using var coordinator = new TailcatServerSessionLifetimeCoordinator(hub, lifetime);

        await hub.StartServerAsync(Request());
        await hub.StopServerAsync();
        hub.RaiseChangedForTest(); // the duplicate, state-unchanged raise

        Assert.Equal(1, lifetime.StartCalls);
        Assert.Equal(1, lifetime.StopCalls);
        Assert.False(lifetime.IsActive);
    }

    [Fact]
    public async Task StartingImmediatelyAfterAnEndIsNotDisturbedByALateStaleEventFromTheOldServer()
    {
        // The old server's teardown can still have an event in flight (see
        // above) when a brand-new share starts right after. That stray event
        // must not be mistaken for the *new* server ending: the coordinator
        // re-reads Snapshot.Server fresh, and by the time the stale event
        // arrives the snapshot already reflects the new server.
        var hub = new FakeHub();
        var lifetime = new FakeActiveSessionLifetime();
        using var coordinator = new TailcatServerSessionLifetimeCoordinator(hub, lifetime);

        await hub.StartServerAsync(Request());
        await hub.StopServerAsync();
        Assert.False(lifetime.IsActive);

        await hub.StartServerAsync(Request());
        Assert.Equal(2, lifetime.StartCalls);
        Assert.True(lifetime.IsActive);

        // The stale, late-arriving duplicate from the *old* server's disposal.
        hub.RaiseChangedForTest();

        Assert.Equal(2, lifetime.StartCalls);
        Assert.Equal(1, lifetime.StopCalls);
        Assert.True(lifetime.IsActive);
    }

    [Fact]
    public async Task NeverStartingAServerNeverTouchesTheLifetime()
    {
        var hub = new FakeHub();
        var lifetime = new FakeActiveSessionLifetime();
        using var coordinator = new TailcatServerSessionLifetimeCoordinator(hub, lifetime);

        // Unrelated hub activity (nothing here starts a server) must not
        // acquire a lifetime the server itself never needed.
        hub.RaiseChangedForTest();

        Assert.Equal(0, lifetime.StartCalls);
        Assert.Equal(0, lifetime.StopCalls);
        Assert.False(lifetime.IsActive);
    }

    private static TailcatServeRequest Request() => new(
        Lifetime: TimeSpan.FromMinutes(30),
        AllowedClientKeys: "nodekey:test-client",
        EnableShell: false,
        AuthorizedSshKeys: null,
        EnableFiles: false,
        SharedFolder: null,
        FileMode: "ro",
        EnableExitNode: false,
        ServeTargets: ["8080"]);

    /// <summary>
    /// Stands in for MeowshellTailcatHubService, with the two extra test
    /// hooks the real implementation has no equivalent surface for:
    /// <see cref="SimulateDeadlineExpiryAsync"/> (a spontaneous end that
    /// never goes through <see cref="StopServerAsync"/>, matching
    /// ObserveServerAsync) and <see cref="RaiseChangedForTest"/> (a bare,
    /// state-unchanged Changed raise, matching the hub's real duplicate-raise
    /// behavior around disposal).
    /// </summary>
    private sealed class FakeHub : ITailcatHubService
    {
        private TailcatServerSnapshot? _server;

        public event EventHandler? Changed;
        public TailcatHubSnapshot Snapshot => new(_server, null, [], []);

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
        {
            if (_server is not null) throw new InvalidOperationException("Tailcat sharing is already running.");
            _server = new TailcatServerSnapshot(
                "tc-test-address", DateTimeOffset.UtcNow + request.Lifetime,
                request.EnableShell, request.EnableFiles, request.EnableExitNode, request.AllowAnyClient,
                request.UseTailcatCredentialForShell, request.SharedFolder, request.FileMode, request.ServeTargets ?? []);
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task StopServerAsync(CancellationToken cancellationToken = default)
        {
            _server = null;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task SimulateDeadlineExpiryAsync()
        {
            _server = null;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public void RaiseChangedForTest() => RaiseChanged();

        public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopSocksAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) => Task.FromResult("nodekey:test");
        public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
