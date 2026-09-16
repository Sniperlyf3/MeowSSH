using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class PortForwardProfileServiceTests
{
    [Fact]
    public async Task FreeTierCannotAccessProfiles()
    {
        var store = new MemoryPortForwardProfileStore();
        var service = new PortForwardProfileService(store, new FakeEntitlements(false));
        var profile = Profile();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(profile));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task ProfilesAreReusableAndNormalized()
    {
        var store = new MemoryPortForwardProfileStore();
        var service = new PortForwardProfileService(store, new FakeEntitlements(true));

        var saved = await service.SaveAsync(Profile() with { Label = " Web ", ListenAddress = " 127.0.0.1:8080 " });
        await service.SaveAsync(Profile() with { Label = "Database", ListenAddress = "127.0.0.1:5432", Destination = "db.internal:5432" });

        var result = await service.GetAllAsync();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Id == saved.Id && item.Label == "Web" && item.ListenAddress == "127.0.0.1:8080");
    }

    [Fact]
    public async Task SocksProfileContainsNoCredentialFields()
    {
        var store = new MemoryPortForwardProfileStore();
        var service = new PortForwardProfileService(store, new FakeEntitlements(true));
        var profile = Profile() with
        {
            Kind = SshForwardKind.Socks,
            Destination = null,
            RequireSocksAuth = true,
        };

        var saved = await service.SaveAsync(profile);

        Assert.True(saved.RequireSocksAuth);
        Assert.DoesNotContain("Password", saved.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("Username", saved.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task RemoteUnixSocketProfileIsRejected()
    {
        var service = new PortForwardProfileService(new MemoryPortForwardProfileStore(), new FakeEntitlements(true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            Profile() with { Kind = SshForwardKind.Remote, UseUnixSocket = true }));
    }

    private static PortForwardProfile Profile() => new(
        Guid.Empty,
        "Web",
        SshForwardKind.Local,
        "127.0.0.1:8080",
        "127.0.0.1:80",
        false,
        true,
        256,
        false,
        default);

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

        public bool Has(PremiumFeature feature) => pro && feature == PremiumFeature.PortForwardProfiles;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
