using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class SftpBookmarkServiceTests
{
    [Fact]
    public async Task FreeTierCannotReadOrWriteBookmarks()
    {
        var store = new MemorySftpBookmarkStore();
        var service = new SftpBookmarkService(store, new FakeEntitlements(pro: false));
        var hostId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetForHostAsync(hostId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(hostId, "Logs", "/var/log/"));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task SavesNormalizesAndScopesBookmarksByHost()
    {
        var store = new MemorySftpBookmarkStore();
        var service = new SftpBookmarkService(store, new FakeEntitlements(pro: true));
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        var first = await service.SaveAsync(one, " Logs ", " /var/log/ ");
        await service.SaveAsync(two, "Other", "/srv");

        var result = Assert.Single(await service.GetForHostAsync(one));
        Assert.Equal(first.Id, result.Id);
        Assert.Equal("Logs", result.Label);
        Assert.Equal("/var/log", result.Path);
    }

    [Fact]
    public async Task SavingSameHostPathUpdatesLabelInsteadOfDuplicating()
    {
        var store = new MemorySftpBookmarkStore();
        var service = new SftpBookmarkService(store, new FakeEntitlements(pro: true));
        var hostId = Guid.NewGuid();

        var first = await service.SaveAsync(hostId, "Logs", "/var/log");
        var second = await service.SaveAsync(hostId, "System logs", "/var/log/");

        Assert.Equal(first.Id, second.Id);
        var stored = Assert.Single(await service.GetForHostAsync(hostId));
        Assert.Equal("System logs", stored.Label);
    }

    [Fact]
    public async Task DeleteRemovesBookmark()
    {
        var store = new MemorySftpBookmarkStore();
        var service = new SftpBookmarkService(store, new FakeEntitlements(pro: true));
        var bookmark = await service.SaveAsync(Guid.NewGuid(), "Home", "/home/meow");

        await service.DeleteAsync(bookmark.Id);

        Assert.Empty(await store.GetAllAsync());
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue)
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => pro && feature == PremiumFeature.AdvancedSftp;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
