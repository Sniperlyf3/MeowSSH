using MeowSSH.Core.Licensing;
using MeowSSH.UI.Services;

namespace MeowSSH.UI.Tests;

public sealed class EntitlementTailcatVpnControllerTests
{
    [Fact]
    public async Task FreeTierCannotStartVpn()
    {
        var inner = new FakeVpnController();
        var gate = new EntitlementTailcatVpnController(inner, new FakeEntitlements(EntitlementTier.Free));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.StartAsync(new TailcatVpnRequest("127.0.0.1:1080", ["0.0.0.0/0"])));

        Assert.True(error.Message.Contains("Pro", StringComparison.Ordinal));
        Assert.Equal(0, inner.StartCount);
    }

    [Fact]
    public async Task ProTierCanStartVpn()
    {
        var inner = new FakeVpnController();
        var gate = new EntitlementTailcatVpnController(inner, new FakeEntitlements(EntitlementTier.Pro));

        await gate.StartAsync(new TailcatVpnRequest("127.0.0.1:1080", ["0.0.0.0/0"]));

        Assert.Equal(1, inner.StartCount);
    }

    [Fact]
    public async Task StopRemainsAvailableWithoutEntitlement()
    {
        var inner = new FakeVpnController();
        var gate = new EntitlementTailcatVpnController(inner, new FakeEntitlements(EntitlementTier.Free));

        await gate.StopAsync();

        Assert.Equal(1, inner.StopCount);
    }

    private sealed class FakeVpnController : ITailcatVpnController
    {
        public TailcatVpnSnapshot Snapshot { get; } = new(false, null, []);
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEntitlements(EntitlementTier tier) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = new(
            tier,
            tier == EntitlementTier.Free ? EntitlementSource.None : EntitlementSource.ServerVerifiedGooglePlay,
            DateTimeOffset.UtcNow,
            tier == EntitlementTier.Free ? null : DateTimeOffset.UtcNow.AddDays(1));

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) =>
            EntitlementPolicy.Allows(Current, feature, DateTimeOffset.UtcNow);

        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }
}
