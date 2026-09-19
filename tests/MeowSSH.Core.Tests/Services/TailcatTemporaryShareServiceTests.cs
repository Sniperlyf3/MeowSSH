using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

/// <summary>
/// A temporary share is a live credential (the Tailcat address itself, per
/// Meowshell/CLAUDE.md) with a timer attached, so "expired" has to mean the
/// underlying process is actually gone, not just that a stored flag says so.
/// <see cref="FakeHub"/> models the real deadline path end to end -- a
/// background delay that flips <see cref="TailcatHubSnapshot.Server"/> to null
/// and raises <see cref="ITailcatHubService.Changed"/> on its own, exactly like
/// <c>MeowshellServer</c>'s internal deadline timer driving
/// <c>MeowshellTailcatHubService.ObserveServerAsync</c> (confirmed by reading
/// both) -- so the expiry tests below genuinely wait for wall-clock time to
/// pass rather than asserting on a state that never depended on a clock.
/// </summary>
public sealed class TailcatTemporaryShareServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FreeTierCannotCreateAShare()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Free);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Request()));

        Assert.Contains("Pro", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, hub.StartCalls);
    }

    [Fact]
    public async Task CreateRequiresAtLeastOneServeTarget()
    {
        var service = NewService(new FakeHub(), EntitlementTier.Pro);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Request(targets: [])));
    }

    [Fact]
    public async Task CreateRequiresAtLeastOneAllowedClientKey()
    {
        var service = NewService(new FakeHub(), EntitlementTier.Pro);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Request(clientKeys: "")));

        // The precise wording matters: a temporary share bounds *when* an
        // address works, never *who* may use it. Losing this message would
        // let someone believe "allow any client" is on the table here.
        Assert.Contains("not who may use it", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public async Task CreateRejectsLifetimeOutsideOneSecondToTwentyFourHours(int hours)
    {
        var service = NewService(new FakeHub(), EntitlementTier.Pro);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAsync(Request(lifetime: TimeSpan.FromHours(hours))));
    }

    [Fact]
    public async Task CreatedShareNeverGrantsShellFilesExitNodeOrAnyClient()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);

        await service.CreateAsync(Request(targets: ["8080", "8080", " 443 "]));

        Assert.NotNull(hub.LastRequest);
        Assert.False(hub.LastRequest!.EnableShell);
        Assert.False(hub.LastRequest.EnableFiles);
        Assert.False(hub.LastRequest.EnableExitNode);
        Assert.False(hub.LastRequest.AllowAnyClient);
        // EphemeralKey follows from PrivateKeyJson being null: every share mints
        // a brand-new Tailcat identity, so a later share can never be handed
        // the same address as one that already ended.
        Assert.Null(hub.LastRequest.PrivateKeyJson);
        // Deduplicated/trimmed, matching Normalize().
        Assert.Equal(["8080", "443"], hub.LastRequest.ServeTargets);
    }

    [Fact]
    public async Task OnlyOneShareCanBeActiveAtOnce()
    {
        var service = NewService(new FakeHub(), EntitlementTier.Pro);
        await service.CreateAsync(Request());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Request()));
        Assert.Contains("Revoke", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeStopsTheRealHubAndRecordsRevokedNotExpired()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var share = await service.CreateAsync(Request());
        await service.RevokeAsync();

        // This is the crux of "genuine, not cosmetic" revocation: the fake
        // stands in for MeowshellServer's SIGTERM/SIGKILL, and this assertion
        // is the one that would fail if RevokeAsync only flipped a local flag
        // instead of calling through to the hub.
        Assert.Equal(1, hub.StopCalls);
        Assert.Null(service.Active);
        Assert.Null(hub.Snapshot.Server);

        var history = await service.GetHistoryAsync();
        var recorded = Assert.Single(history, item => item.Id == share.Id);
        Assert.Equal(TailcatTemporaryShareEndReason.Revoked, recorded.EndReason);
        Assert.NotNull(recorded.EndedAtUtc);
        Assert.True(changes >= 2); // at least: created, revoked
    }

    [Fact]
    public async Task RevokeIsANoOpWhenNothingIsActive()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);

        await service.RevokeAsync();

        Assert.Equal(0, hub.StopCalls);
        Assert.Empty(await service.GetHistoryAsync());
    }

    [Fact]
    public async Task ActiveGoesFalseTheMomentTheHubReportsTheServerGoneNotOnlyAfterRevokeAsyncIsCalled()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);
        await service.CreateAsync(Request());

        // Simulates the server being stopped from somewhere this service was
        // never told about -- e.g. the general "Share this phone" panel, or a
        // crash -- without going through RevokeAsync at all.
        await hub.StopServerAsync();

        Assert.Null(service.Active);
        var history = await service.GetHistoryAsync();
        Assert.Equal(TailcatTemporaryShareEndReason.Expired, history.Single().EndReason);
    }

    [Fact]
    public async Task ADeadlineThatActuallyElapsesEndsTheShareAsExpired()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);
        var expiredSignal = new TaskCompletionSource();
        service.Changed += (_, _) => { if (service.Active is null) expiredSignal.TrySetResult(); };

        var share = await service.CreateAsync(Request(lifetime: TimeSpan.FromMilliseconds(150)));

        // Immediately after creating, real time has not advanced past the
        // deadline yet -- the share must still read active. Without this
        // assertion, a broken test could pass merely because Active always
        // ends up null eventually, regardless of whether anything timed the
        // wait to the actual lifetime.
        Assert.NotNull(service.Active);
        Assert.Equal(share.Id, service.Active!.Id);

        await expiredSignal.Task.WaitAsync(WaitTimeout);

        Assert.Null(service.Active);
        var recorded = (await service.GetHistoryAsync()).Single(item => item.Id == share.Id);
        Assert.Equal(TailcatTemporaryShareEndReason.Expired, recorded.EndReason);
        Assert.True(recorded.EndedAtUtc >= recorded.CreatedAtUtc + TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task HistoryListsMostRecentFirstIncludingEndedShares()
    {
        var hub = new FakeHub();
        var service = NewService(hub, EntitlementTier.Pro);

        var first = await service.CreateAsync(Request(label: "First"));
        await service.RevokeAsync();
        await Task.Delay(5); // keep CreatedAtUtc strictly increasing across the two shares
        var second = await service.CreateAsync(Request(label: "Second"));

        var history = await service.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(second.Id, history[0].Id);
        Assert.Null(history[0].EndedAtUtc);
        Assert.Equal(first.Id, history[1].Id);
        Assert.Equal(TailcatTemporaryShareEndReason.Revoked, history[1].EndReason);
    }

    private static TailcatTemporaryShareService NewService(FakeHub hub, EntitlementTier tier) =>
        new(new MemoryTailcatTemporaryShareStore(), hub, new FakeEntitlements(tier));

    private static TailcatTemporaryShareRequest Request(
        string label = "Test share",
        IReadOnlyList<string>? targets = null,
        string clientKeys = "nodekey:test-client",
        TimeSpan? lifetime = null) =>
        new(label, targets ?? ["8080"], clientKeys, lifetime ?? TimeSpan.FromMinutes(30));

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

    /// <summary>
    /// Stands in for <c>MeowshellTailcatHubService</c>, but with its own real
    /// (short) deadline timer instead of a native subprocess -- see the type
    /// remarks above for why that matters for the expiry tests.
    /// </summary>
    private sealed class FakeHub : ITailcatHubService
    {
        private TailcatServerSnapshot? _server;
        private CancellationTokenSource? _deadline;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public TailcatServeRequest? LastRequest { get; private set; }

        public TailcatHubSnapshot Snapshot => new(_server, null, [], []);
        public event EventHandler? Changed;

        public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
        {
            if (_server is not null) throw new InvalidOperationException("Tailcat sharing is already running.");
            StartCalls++;
            LastRequest = request;
            _server = new TailcatServerSnapshot(
                "tc-test-temporary-share",
                DateTimeOffset.UtcNow + request.Lifetime,
                request.EnableShell, request.EnableFiles, request.EnableExitNode, request.AllowAnyClient,
                request.UseTailcatCredentialForShell, request.SharedFolder, request.FileMode, request.ServeTargets ?? []);

            _deadline?.Cancel();
            var cts = new CancellationTokenSource();
            _deadline = cts;
            _ = RunDeadlineAsync(request.Lifetime, cts.Token);

            RaiseChanged();
            return Task.CompletedTask;
        }

        private async Task RunDeadlineAsync(TimeSpan lifetime, CancellationToken token)
        {
            try { await Task.Delay(lifetime, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            _server = null;
            RaiseChanged();
        }

        public Task StopServerAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            _deadline?.Cancel();
            _server = null;
            RaiseChanged();
            return Task.CompletedTask;
        }

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
