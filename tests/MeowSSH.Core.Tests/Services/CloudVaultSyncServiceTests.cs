using System.Security.Cryptography;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Storage;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

/// <summary>
/// Two (or three) phones, each with a real vault, real encryption and the real
/// restore path; only the network is faked, by an in-memory server with the
/// same compare-and-swap rule as MeowSSHAPI's sync slot.
/// </summary>
public sealed class CloudVaultSyncServiceTests
{
    private static Argon2idKeyDerivation Kdf => new(KdfParameters.Testing);
    /// <summary>
    /// Only explicit SyncNowAsync calls, so each test decides exactly when each
    /// phone syncs. The automatic path has its own tests below.
    /// </summary>
    private static readonly CloudSyncSchedule Manual = new(Debounce: null, Interval: null);
    private static readonly CloudSyncSchedule Immediate = new(TimeSpan.Zero, Interval: null);

    private sealed class FixedIdentity(string id) : IDeviceIdentity
    {
        public ValueTask<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(id);
    }

    private sealed class Phone : IDisposable
    {
        public string Id { get; }
        public InMemoryVaultStorage Storage { get; } = new();
        public FakeDeviceKeyStore Keys { get; } = new();
        public VaultStore Vault { get; }
        public MemoryCloudBackupCredentialStore Credentials { get; } = new();
        public MemoryCloudSyncCheckpointStore Checkpoints { get; } = new();
        public FakeTier Entitlement { get; }
        public CloudVaultBackupService Backup { get; }
        public CloudVaultSyncService Sync { get; }

        public Phone(string id, FakeCloudBackupApi cloud, EntitlementTier tier = EntitlementTier.ProCloud, CloudSyncSchedule? schedule = null)
        {
            Id = id;
            Vault = new VaultStore(Storage);
            Entitlement = new FakeTier(tier);
            var local = new EncryptedVaultBackupService(Storage, Vault, Keys, Entitlement);
            var grants = new FakeCloudGrants(tier);
            Backup = new CloudVaultBackupService(Storage, local, cloud, grants, Credentials, Entitlement);
            Sync = new CloudVaultSyncService(Vault, Storage, cloud, grants, Credentials, Checkpoints, new FixedIdentity(id), Entitlement, schedule ?? Manual);
        }

        public IEnumerable<string> Labels => Vault.Document.LiveHosts.Select(h => h.Label).Order();

        public ValueTask AddHostAsync(string label) =>
            Vault.UpdateAsync((document, now) => document.WithHost(new HostRecord
            {
                Id = Guid.NewGuid(), Label = label, Address = "10.0.0.1", Username = "deploy",
            }, now));

        public HostRecord Find(string label) => Vault.Document.LiveHosts.Single(h => h.Label == label);

        public void Dispose()
        {
            Sync.Dispose();
            Vault.Dispose();
        }
    }

    /// <summary>Phone A with a fresh vault and sync on; phone B restored from A's cloud backup, sync on too.</summary>
    private static async Task<(Phone A, Phone B)> TwoSyncedPhonesAsync(FakeCloudBackupApi cloud, CloudSyncSchedule? schedule = null)
    {
        var a = new Phone("phone-a", cloud, schedule: schedule);
        var code = await a.Vault.CreateAsync(a.Keys, "phone-a", Kdf);
        await a.AddHostAsync("shared-1");
        await a.Backup.EnableAsync(code);
        var backup = await a.Backup.BackupNowAsync();
        await a.Sync.EnableAsync();

        var b = new Phone("phone-b", cloud, schedule: schedule);
        await b.Vault.CreateAsync(b.Keys, "phone-b", Kdf);
        await b.Backup.RestoreAsync(code, backup.Id);
        await b.Sync.EnableAsync();
        return (a, b);
    }

    [Fact]
    public async Task AnEditOnEitherPhoneReachesTheOther()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;

        await a.AddHostAsync("added-on-a");
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        Assert.Contains("added-on-a", b.Labels);

        await b.AddHostAsync("added-on-b");
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        Assert.Equal(a.Labels, b.Labels);
        Assert.Contains("added-on-b", a.Labels);
    }

    [Fact]
    public async Task EditsBothPhonesMadeWithoutSyncingBothSurvive()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;

        await a.AddHostAsync("offline-a");
        await b.AddHostAsync("offline-b");
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();

        Assert.Equal(["offline-a", "offline-b", "shared-1"], a.Labels);
        Assert.Equal(a.Labels, b.Labels);
    }

    [Fact]
    public async Task TheSameHostEditedOnBothPhonesEndsUpWithTheLaterEditOnBoth()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        var id = a.Find("shared-1").Id;
        var t = DateTimeOffset.UtcNow;

        await a.Vault.UpdateAsync(d => d.WithHost(d.Hosts.Single(h => h.Id == id) with { Label = "renamed-first-on-a" }, t));
        await b.Vault.UpdateAsync(d => d.WithHost(d.Hosts.Single(h => h.Id == id) with { Label = "renamed-later-on-b" }, t.AddSeconds(5)));
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Equal(["renamed-later-on-b"], a.Labels);
        Assert.Equal(["renamed-later-on-b"], b.Labels);
    }

    [Fact]
    public async Task DeletingAHostOnOnePhoneDeletesItOnTheOther()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;

        await a.Vault.UpdateAsync((d, now) => d.WithoutHost(a.Find("shared-1").Id, now));
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Empty(b.Labels);
    }

    [Fact]
    public async Task APushThatLosesARaceIsRetriedWithoutDroppingEitherPhonesEdit()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.AddHostAsync("racing-a");
        await b.AddHostAsync("racing-b");

        // B completes a whole sync in the gap between A's fetch and A's push,
        // so A's push names a version that is no longer current.
        cloud.BeforePush = () => b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();

        Assert.Equal(["racing-a", "racing-b", "shared-1"], a.Labels);
        Assert.Equal(a.Labels, b.Labels);
    }

    [Fact]
    public async Task ASyncWithNothingNewOnEitherSideUploadsNothing()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.Sync.SyncNowAsync();
        var pushes = cloud.SyncPushes.Count;

        await a.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();

        Assert.Equal(pushes, cloud.SyncPushes.Count);
    }

    [Fact]
    public async Task APhoneThatOnlyReceivedChangesDoesNotUploadThemBack()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.AddHostAsync("from-a");
        await a.Sync.SyncNowAsync();
        var pushes = cloud.SyncPushes.Count;

        await b.Sync.SyncNowAsync();

        Assert.Contains("from-a", b.Labels);
        Assert.Equal(pushes, cloud.SyncPushes.Count);
    }

    [Fact]
    public async Task TheServerOnlyEverReceivesTheEncryptedVaultFile()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.AddHostAsync("secret-production-host");

        await a.Sync.SyncNowAsync();

        var pushed = cloud.SyncPushes[^1];
        Assert.Equal(await a.Storage.ReadAsync(), pushed);
        Assert.DoesNotContain("secret-production-host", System.Text.Encoding.UTF8.GetString(pushed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditsAreAttributedToThePhoneThatMadeThemAfterARestore()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;

        await b.AddHostAsync("made-on-b");

        Assert.Equal("phone-b", b.Vault.Document.DeviceId);
        Assert.Equal("phone-b", b.Find("made-on-b").OriginDeviceId);
    }

    [Theory]
    [InlineData(EntitlementTier.Free)]
    [InlineData(EntitlementTier.Pro)]
    public async Task OnlyProCloudCanTurnOnSync(EntitlementTier tier)
    {
        using var phone = new Phone("phone", new FakeCloudBackupApi(), tier);
        await phone.Vault.CreateAsync(phone.Keys, "phone", Kdf);

        Assert.False(phone.Sync.CanSync);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Sync.EnableAsync());
        Assert.Contains("Pro Cloud", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALapsedSubscriptionPausesSyncWithoutTouchingTheVault()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.AddHostAsync("unsynced");
        var pushes = cloud.SyncPushes.Count;

        a.Entitlement.Tier = EntitlementTier.Pro;

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Sync.SyncNowAsync());
        Assert.Equal(pushes, cloud.SyncPushes.Count);
        Assert.Contains("unsynced", a.Labels);
        Assert.NotNull(a.Sync.Status.LastError);
    }

    [Fact]
    public async Task SyncCannotBeTurnedOnBeforeCloudBackup()
    {
        // Sync shares cloud backup's recovery-code identity; without it there
        // is nowhere for another phone to find this vault.
        using var phone = new Phone("phone", new FakeCloudBackupApi());
        await phone.Vault.CreateAsync(phone.Keys, "phone", Kdf);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Sync.EnableAsync());

        Assert.Contains("cloud backup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACloudCopyDeletedFromAnotherPhoneIsNotQuietlyReuploaded()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await b.Sync.SyncNowAsync();

        await a.Backup.DeleteAllAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Sync.SyncNowAsync());
        Assert.Contains("deleted", error.Message, StringComparison.Ordinal);
        Assert.False(b.Sync.Status.Enabled);
        Assert.Null(cloud.SyncedCopy((await b.Credentials.LoadAsync())!.Locator));
    }

    [Fact]
    public async Task ASyncedCopyThisVaultsKeyCannotOpenChangesNothingOnThePhone()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;

        // Plant a different vault's file in A's slot, as a corrupted or
        // mismatched upload would look.
        using var stranger = new Phone("stranger", cloud);
        await stranger.Vault.CreateAsync(stranger.Keys, "stranger", Kdf);
        await stranger.AddHostAsync("not-yours");
        var credential = (await a.Credentials.LoadAsync())!;
        var current = cloud.SyncedCopy(credential.Locator)!;
        await cloud.PushSyncAsync(credential, new SignedEntitlementGrant("p", "s"), (await stranger.Storage.ReadAsync())!,
            Convert.ToHexStringLower(SHA256.HashData(current)));
        var before = await a.Storage.ReadAsync();

        var error = await Assert.ThrowsAsync<CloudBackupException>(() => a.Sync.SyncNowAsync());

        Assert.Equal("sync_unreadable", error.Code);
        Assert.Equal(before, await a.Storage.ReadAsync());
        Assert.DoesNotContain("not-yours", a.Labels);
    }

    [Fact]
    public async Task TurningSyncOffWhileASyncIsRunningStaysOff()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        await a.AddHostAsync("in-flight");

        // The user turns sync off while A's push is on the wire.
        Task? disabling = null;
        cloud.BeforePush = () =>
        {
            disabling = a.Sync.DisableAsync();
            return Task.CompletedTask;
        };
        await a.Sync.SyncNowAsync();
        await disabling!;

        Assert.False((await a.Checkpoints.LoadAsync()).Enabled);
        Assert.False(a.Sync.Status.Enabled);
    }

    [Fact]
    public async Task ALockedVaultIsNotSynced()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud);
        using var _ = a;
        using var __ = b;
        a.Vault.Lock();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => a.Sync.SyncNowAsync());

        Assert.Contains("Unlock", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditIsPushedAutomaticallyWithoutPressingSync()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud, Immediate);
        using var _ = a;
        using var __ = b;
        await a.Sync.WhenAutoSyncIdleAsync();
        await b.Sync.WhenAutoSyncIdleAsync();

        await a.AddHostAsync("pushed-by-itself");
        await a.Sync.WhenAutoSyncIdleAsync();
        await b.Sync.SyncNowAsync();

        Assert.Contains("pushed-by-itself", b.Labels);
        Assert.Null(a.Sync.Status.LastError);
    }

    [Fact]
    public async Task UnlockingPullsWhatOtherPhonesChangedWhileLocked()
    {
        var cloud = new FakeCloudBackupApi();
        var (a, b) = await TwoSyncedPhonesAsync(cloud, Immediate);
        using var _ = a;
        using var __ = b;
        await a.Sync.WhenAutoSyncIdleAsync();
        await b.Sync.WhenAutoSyncIdleAsync();
        a.Vault.Lock();

        await b.AddHostAsync("while-a-was-locked");
        await b.Sync.WhenAutoSyncIdleAsync();
        await a.Vault.UnlockWithDeviceKeyAsync(a.Keys);
        await a.Sync.WhenAutoSyncIdleAsync();

        Assert.Contains("while-a-was-locked", a.Labels);
    }
}
