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
        var profile = Profile(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetForHostAsync(profile.HostId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(profile));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task ProfilesAreScopedToHostAndNormalized()
    {
        var store = new MemoryPortForwardProfileStore();
        var service = new PortForwardProfileService(store, new FakeEntitlements(true));
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        var saved = await service.SaveAsync(Profile(one) with { Label = " Web ", ListenAddress = " 127.0.0.1:8080 " });
        await service.SaveAsync(Profile(two) with { Label = "Other" });

        var result = Assert.Single(await service.GetForHostAsync(one));
        Assert.Equal(saved.Id, result.Id);
        Assert.Equal("Web", result.Label);
        Assert.Equal("127.0.0.1:8080", result.ListenAddress);
    }

    [Fact]
    public async Task SocksProfileContainsNoCredentialFields()
    {
        var store = new MemoryPortForwardProfileStore();
        var service = new PortForwardProfileService(store, new FakeEntitlements(true));
        var profile = Profile(Guid.NewGuid()) with
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
            Profile(Guid.NewGuid()) with { Kind = SshForwardKind.Remote, UseUnixSocket = true }));
    }

    private static PortForwardProfile Profile(Guid hostId) => new(
        Guid.Empty,
        hostId,
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
