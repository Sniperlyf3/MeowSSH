using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Storage;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

public sealed class EncryptedVaultBackupServiceTests
{
    private static Argon2idKeyDerivation Kdf => new(KdfParameters.Testing);

    [Fact]
    public async Task ExportReturnsExistingEncryptedVaultWithoutPlaintextSecrets()
    {
        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();
        using var vault = new VaultStore(storage);
        await vault.CreateAsync(keys, "source-device", Kdf);
        await vault.UpdateAsync((document, now) => document.WithHost(Host("secret-production-host"), now));
        var service = Service(storage, vault, keys, pro: true);

        var exported = await service.ExportAsync();
        var stored = await storage.ReadAsync();

        Assert.Equal(stored, exported);
        Assert.DoesNotContain("secret-production-host", Encoding.UTF8.GetString(exported), StringComparison.Ordinal);
        var info = await service.InspectAsync(exported);
        Assert.Equal("source-device", info.DeviceId);
        Assert.True(info.Revision > 0);
    }

    [Fact]
    public async Task FreeTierCannotExportOrRestore()
    {
        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();
        using var vault = new VaultStore(storage);
        await vault.CreateAsync(keys, "device", Kdf);
        var backup = (await storage.ReadAsync())!;
        var service = Service(storage, vault, keys, pro: false);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExportAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.RestoreAsync(backup, "ignored"));
    }

    [Fact]
    public async Task CorrectRecoveryCodeRestoresAnotherDevicesVault()
    {
        var sourceStorage = new InMemoryVaultStorage();
        var sourceKeys = new FakeDeviceKeyStore();
        using var sourceVault = new VaultStore(sourceStorage);
        var sourceRecovery = await sourceVault.CreateAsync(sourceKeys, "source-device", Kdf);
        await sourceVault.UpdateAsync((document, now) => document.WithHost(Host("restored-host"), now));
        var backup = (await sourceStorage.ReadAsync())!;

        var targetStorage = new InMemoryVaultStorage();
        var targetKeys = new FakeDeviceKeyStore();
        using var targetVault = new VaultStore(targetStorage);
        await targetVault.CreateAsync(targetKeys, "target-device", Kdf);
        await targetVault.UpdateAsync((document, now) => document.WithHost(Host("old-host"), now));
        var service = Service(targetStorage, targetVault, targetKeys, pro: true);

        await service.RestoreAsync(backup, sourceRecovery);

        Assert.True(targetVault.IsUnlocked);
        Assert.Equal("source-device", targetVault.Document.DeviceId);
        Assert.Contains(targetVault.Document.LiveHosts, host => host.Label == "restored-host");
        Assert.DoesNotContain(targetVault.Document.LiveHosts, host => host.Label == "old-host");
    }

    [Fact]
    public async Task WrongRecoveryCodeLeavesCurrentVaultByteForByteUntouched()
    {
        var sourceStorage = new InMemoryVaultStorage();
        var sourceKeys = new FakeDeviceKeyStore();
        using var sourceVault = new VaultStore(sourceStorage);
        await sourceVault.CreateAsync(sourceKeys, "source-device", Kdf);
        var backup = (await sourceStorage.ReadAsync())!;

        var targetStorage = new InMemoryVaultStorage();
        var targetKeys = new FakeDeviceKeyStore();
        using var targetVault = new VaultStore(targetStorage);
        await targetVault.CreateAsync(targetKeys, "target-device", Kdf);
        var before = (await targetStorage.ReadAsync())!;
        var service = Service(targetStorage, targetVault, targetKeys, pro: true);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await service.RestoreAsync(backup, RecoveryCode.Generate()));

        var after = await targetStorage.ReadAsync();
        Assert.Equal(before, after);
        Assert.True(targetVault.IsUnlocked);
        Assert.Equal("target-device", targetVault.Document.DeviceId);
    }

    [Fact]
    public async Task TruncatedAndOversizedFilesAreRejectedBeforeRestore()
    {
        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();
        using var vault = new VaultStore(storage);
        await vault.CreateAsync(keys, "device", Kdf);
        var service = Service(storage, vault, keys, pro: true);

        await Assert.ThrowsAnyAsync<Exception>(async () => await service.InspectAsync("MEOWVLT1"u8.ToArray()));
        var oversized = new byte[EncryptedVaultBackupService.MaxBackupBytes + 1];
        var error = await Assert.ThrowsAsync<VaultFormatException>(async () => await service.InspectAsync(oversized));
        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeviceKeyRebindIsExplicitAfterRestore()
    {
        var sourceStorage = new InMemoryVaultStorage();
        var sourceKeys = new FakeDeviceKeyStore();
        using var sourceVault = new VaultStore(sourceStorage);
        var recovery = await sourceVault.CreateAsync(sourceKeys, "source", Kdf);
        var backup = (await sourceStorage.ReadAsync())!;

        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();
        using var vault = new VaultStore(storage);
        await vault.CreateAsync(keys, "target", Kdf);
        var keysBeforeRestore = keys.KeysCreated;
        var service = Service(storage, vault, keys, pro: true);

        await service.RestoreAsync(backup, recovery);
        Assert.Equal(keysBeforeRestore, keys.KeysCreated);

        await service.RebindDeviceKeyAsync();
        Assert.Equal(keysBeforeRestore + 1, keys.KeysCreated);
        vault.Lock();
        await vault.UnlockWithDeviceKeyAsync(keys);
        Assert.Equal("source", vault.Document.DeviceId);
    }

    private static EncryptedVaultBackupService Service(
        IVaultStorage storage,
        VaultStore vault,
        IDeviceKeyStore keys,
        bool pro) => new(storage, vault, keys, new FakeEntitlements(pro));

    private static HostRecord Host(string label) => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        Address = "10.0.0.10",
        Username = "deploy",
    };

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current => pro
            ? new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow)
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => pro;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }
}
