using System.Security.Cryptography;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Storage;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

/// <summary>
/// Real vaults, real encryption and the real restore path; only the network
/// is faked, by an in-memory server that enforces the same ownership rule as
/// MeowSSHAPI (the bearer secret must hash to the locator).
/// </summary>
public sealed class CloudVaultBackupServiceTests
{
    private static Argon2idKeyDerivation Kdf => new(KdfParameters.Testing);

    private sealed class Phone : IDisposable
    {
        public InMemoryVaultStorage Storage { get; } = new();
        public FakeDeviceKeyStore Keys { get; } = new();
        public VaultStore Vault { get; }
        public MemoryCloudBackupCredentialStore Credentials { get; } = new();
        public FakeTier Entitlement { get; }
        public CloudVaultBackupService Cloud { get; }
        public string RecoveryCode { get; private set; } = "";

        public Phone(FakeCloudBackupApi cloud, EntitlementTier tier)
        {
            Vault = new VaultStore(Storage);
            Entitlement = new FakeTier(tier);
            var local = new EncryptedVaultBackupService(Storage, Vault, Keys, Entitlement);
            Cloud = new CloudVaultBackupService(Storage, local, cloud, new FakeCloudGrants(tier), Credentials, Entitlement);
        }

        public async Task<Phone> WithVaultAsync(string deviceId, string hostLabel)
        {
            RecoveryCode = await Vault.CreateAsync(Keys, deviceId, Kdf);
            await Vault.UpdateAsync((document, now) => document.WithHost(new HostRecord
            {
                Id = Guid.NewGuid(), Label = hostLabel, Address = "10.0.0.1", Username = "deploy",
            }, now));
            return this;
        }

        public void Dispose() => Vault.Dispose();
    }

    [Fact]
    public async Task ABackupTakenOnOnePhoneRestoresOnANewPhoneFromTheRecoveryCodeAlone()
    {
        var cloud = new FakeCloudBackupApi();
        using var oldPhone = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("old-phone", "prod-db");
        await oldPhone.Cloud.EnableAsync(oldPhone.RecoveryCode);
        await oldPhone.Cloud.BackupNowAsync();

        using var newPhone = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("new-phone", "placeholder");
        var found = Assert.Single(await newPhone.Cloud.FindAsync(oldPhone.RecoveryCode));
        await newPhone.Cloud.RestoreAsync(oldPhone.RecoveryCode, found.Id);

        Assert.Equal("old-phone", newPhone.Vault.Document.DeviceId);
        Assert.Contains(newPhone.Vault.Document.LiveHosts, host => host.Label == "prod-db");
        // Backups continue from the new phone under the same identity.
        Assert.True((await newPhone.Cloud.GetStatusAsync()).Enabled);
    }

    [Fact]
    public async Task TheServerOnlyEverReceivesTheEncryptedVaultFile()
    {
        var cloud = new FakeCloudBackupApi();
        using var phone = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("phone", "secret-production-host");
        await phone.Cloud.EnableAsync(phone.RecoveryCode);

        await phone.Cloud.BackupNowAsync();

        var uploaded = Assert.Single(cloud.Uploads);
        Assert.Equal(await phone.Storage.ReadAsync(), uploaded);
        Assert.DoesNotContain("secret-production-host", System.Text.Encoding.UTF8.GetString(uploaded), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongRecoveryCodeCannotTurnOnCloudBackup()
    {
        // Otherwise the phone would upload to a locator derived from a typo,
        // which no new phone could ever find again.
        using var phone = await new Phone(new FakeCloudBackupApi(), EntitlementTier.ProCloud).WithVaultAsync("phone", "h");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            phone.Cloud.EnableAsync(MeowSSH.Core.Security.RecoveryCode.Generate()));

        Assert.Contains("does not unlock", error.Message, StringComparison.Ordinal);
        Assert.Null(await phone.Credentials.LoadAsync());
        Assert.True(phone.Vault.IsUnlocked);
    }

    [Theory]
    [InlineData(EntitlementTier.Free)]
    [InlineData(EntitlementTier.Pro)]
    public async Task OnlyProCloudCanTurnOnOrUploadBackups(EntitlementTier tier)
    {
        using var phone = await new Phone(new FakeCloudBackupApi(), tier).WithVaultAsync("phone", "h");

        Assert.False(phone.Cloud.CanUpload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Cloud.EnableAsync(phone.RecoveryCode));
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Cloud.BackupNowAsync());
    }

    [Fact]
    public async Task ALapsedSubscriberCanStillFindAndRestoreTheirBackup()
    {
        var cloud = new FakeCloudBackupApi();
        using var subscribed = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("phone", "keep-me");
        await subscribed.Cloud.EnableAsync(subscribed.RecoveryCode);
        await subscribed.Cloud.BackupNowAsync();

        // Same person, subscription lapsed, now on the Free tier on a new phone.
        using var lapsed = await new Phone(cloud, EntitlementTier.Free).WithVaultAsync("replacement", "x");
        var version = Assert.Single(await lapsed.Cloud.FindAsync(subscribed.RecoveryCode));
        await lapsed.Cloud.RestoreAsync(subscribed.RecoveryCode, version.Id);

        Assert.Contains(lapsed.Vault.Document.LiveHosts, host => host.Label == "keep-me");
    }

    [Fact]
    public async Task RestoringADifferentVaultLocallyStopsUploadsUnderTheOldIdentity()
    {
        var cloud = new FakeCloudBackupApi();
        using var phone = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("phone", "h");
        await phone.Cloud.EnableAsync(phone.RecoveryCode);

        // Another vault replaces this one through the local-file restore path.
        using var other = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("other", "o");
        var otherFile = (await other.Storage.ReadAsync())!;
        await new EncryptedVaultBackupService(phone.Storage, phone.Vault, phone.Keys, phone.Entitlement)
            .RestoreAsync(otherFile, other.RecoveryCode);

        Assert.True((await phone.Cloud.GetStatusAsync()).NeedsRecoveryCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Cloud.BackupNowAsync());
        Assert.Empty(cloud.Uploads);
    }

    [Fact]
    public async Task DeleteErasesEveryCloudVersionAndForgetsTheCredential()
    {
        var cloud = new FakeCloudBackupApi();
        using var phone = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("phone", "h");
        await phone.Cloud.EnableAsync(phone.RecoveryCode);
        await phone.Cloud.BackupNowAsync();

        await phone.Cloud.DeleteAllAsync();

        Assert.Empty(await phone.Cloud.FindAsync(phone.RecoveryCode));
        Assert.Null(await phone.Credentials.LoadAsync());
    }

    [Fact]
    public async Task ARestoreWithTheWrongRecoveryCodeLeavesTheCurrentVaultUntouched()
    {
        var cloud = new FakeCloudBackupApi();
        using var source = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("source", "s");
        await source.Cloud.EnableAsync(source.RecoveryCode);
        var version = await source.Cloud.BackupNowAsync();

        using var target = await new Phone(cloud, EntitlementTier.ProCloud).WithVaultAsync("target", "t");
        var before = await target.Storage.ReadAsync();

        // A different valid code finds nothing: the server rejects its secret.
        await Assert.ThrowsAsync<CloudBackupException>(() =>
            target.Cloud.RestoreAsync(MeowSSH.Core.Security.RecoveryCode.Generate(), version.Id));

        Assert.Equal(before, await target.Storage.ReadAsync());
        Assert.Equal("target", target.Vault.Document.DeviceId);
    }
}
