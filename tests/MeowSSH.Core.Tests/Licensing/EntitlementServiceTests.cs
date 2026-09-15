using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests.Licensing;

public class EntitlementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidCachedGrantUnlocksPaidFeaturesOffline()
    {
        var cached = PaidGrant(Now.AddHours(6));
        var cache = new FakeCache(cached);
        using var service = new EntitlementService(new FakeProvider(), cache, new FixedTimeProvider(Now));

        await service.InitializeAsync();

        Assert.Equal(cached, service.Current);
        Assert.True(service.Has(PremiumFeature.TailcatWorkspaces));
    }

    [Fact]
    public async Task BackendOutageDoesNotRevokeAStillValidCachedGrant()
    {
        var cached = PaidGrant(Now.AddHours(6));
        var cache = new FakeCache(cached);
        var provider = new FakeProvider { RefreshError = new HttpRequestException("backend unavailable") };
        using var service = new EntitlementService(provider, cache, new FixedTimeProvider(Now));
        await service.InitializeAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());

        Assert.Equal(cached, service.Current);
        Assert.Same(cached, cache.Stored);
        Assert.Equal(0, cache.SaveCount);
        Assert.True(service.Has(PremiumFeature.TailcatWorkspaces));
    }

    [Fact]
    public async Task AuthoritativeFreeRefreshDoesRevokePaidAccess()
    {
        var cached = PaidGrant(Now.AddHours(6));
        var cache = new FakeCache(cached);
        var free = EntitlementSnapshot.Free(Now);
        var provider = new FakeProvider { RefreshResult = free };
        using var service = new EntitlementService(provider, cache, new FixedTimeProvider(Now));
        await service.InitializeAsync();

        var refreshed = await service.RefreshAsync();

        Assert.Equal(free, refreshed);
        Assert.Equal(free, service.Current);
        Assert.Equal(free, cache.Stored);
        Assert.Equal(1, cache.SaveCount);
        Assert.False(service.Has(PremiumFeature.TailcatWorkspaces));
    }

    [Fact]
    public async Task ExpiredCachedGrantDoesNotUnlockPaidFeatures()
    {
        var cache = new FakeCache(PaidGrant(Now.AddSeconds(-1)));
        using var service = new EntitlementService(new FakeProvider(), cache, new FixedTimeProvider(Now));

        await service.InitializeAsync();

        Assert.Equal(EntitlementTier.Free, service.Current.Tier);
        Assert.False(service.Has(PremiumFeature.TailcatWorkspaces));
    }

    [Fact]
    public async Task ExpiredRefreshResponseIsStoredAsFree()
    {
        var cache = new FakeCache();
        var provider = new FakeProvider { RefreshResult = PaidGrant(Now.AddSeconds(-1)) };
        using var service = new EntitlementService(provider, cache, new FixedTimeProvider(Now));

        var refreshed = await service.RefreshAsync();

        Assert.Equal(EntitlementTier.Free, refreshed.Tier);
        Assert.Equal(EntitlementTier.Free, service.Current.Tier);
        Assert.Equal(EntitlementTier.Free, cache.Stored?.Tier);
        Assert.False(service.Has(PremiumFeature.TailcatWorkspaces));
    }

    private static EntitlementSnapshot PaidGrant(DateTimeOffset validUntil) =>
        new(
            EntitlementTier.Pro,
            EntitlementSource.CachedVerifiedGrant,
            Now.AddMinutes(-5),
            validUntil,
            "grant-123");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProvider : IEntitlementGrantProvider
    {
        public EntitlementSnapshot? RefreshResult { get; init; }
        public Exception? RefreshError { get; init; }
        public EntitlementSnapshot? RestoreResult { get; init; }

        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (RefreshError is not null) return Task.FromException<EntitlementSnapshot>(RefreshError);
            return Task.FromResult(RefreshResult ?? EntitlementSnapshot.Free(Now));
        }

        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RestoreResult ?? RefreshResult ?? EntitlementSnapshot.Free(Now));
    }

    private sealed class FakeCache(EntitlementSnapshot? initial = null) : IEntitlementCache
    {
        public EntitlementSnapshot? Stored { get; private set; } = initial;
        public int SaveCount { get; private set; }

        public Task<EntitlementSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored);

        public Task SaveAsync(EntitlementSnapshot entitlement, CancellationToken cancellationToken = default)
        {
            Stored = entitlement;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Stored = null;
            return Task.CompletedTask;
        }
    }
}
