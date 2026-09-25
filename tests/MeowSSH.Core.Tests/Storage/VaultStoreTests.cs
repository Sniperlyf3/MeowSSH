using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Storage;

public class VaultStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Cheap Argon2id parameters: these tests run the derivation dozens of times.</summary>
    private static Argon2idKeyDerivation Kdf => new(KdfParameters.Testing);

    private static (VaultStore Store, InMemoryVaultStorage Storage, FakeDeviceKeyStore Keys) NewStore()
    {
        var storage = new InMemoryVaultStorage();
        var time = new FakeTimeProvider(Now);
        return (new VaultStore(storage, time), storage, new FakeDeviceKeyStore());
    }

    private static HostRecord AHost(string label = "build-01") => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        Address = "10.0.0.4",
        Username = "deploy",
    };

    [Fact]
    public async Task ANewVaultIsUnlockedAndSavedImmediately()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;

        var recoveryCode = await store.CreateAsync(keys, "device-a", Kdf);

        Assert.True(store.IsUnlocked);
        Assert.Equal(1, storage.WriteCount);
        Assert.True(RecoveryCode.IsWellFormed(recoveryCode));
        // Both ways in exist from the first save. Creating the recovery copy later
        // would leave a window where a biometric change destroys the vault.
        Assert.Equal(2, store.Document.WrappedKeys.Count);
    }

    [Fact]
    public async Task CreatingASecondVaultOverTheFirstIsRefused()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);

        // Silently replacing it would destroy every host and key the user had.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(keys, "device-a", Kdf));
    }

    [Fact]
    public async Task HostsSurviveLockingAndUnlocking()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        await store.UpdateAsync((document, now) => document.WithHost(AHost(), now));

        store.Lock();
        Assert.False(store.IsUnlocked);
        await store.UnlockWithDeviceKeyAsync(keys);

        var host = Assert.Single(store.Document.Hosts);
        Assert.Equal("build-01", host.Label);
        // Written to storage, not merely held: a different store over the same
        // bytes is what a restarted app actually does.
        using var restarted = new VaultStore(storage);
        await restarted.UnlockWithDeviceKeyAsync(keys);
        Assert.Single(restarted.Document.Hosts);
    }

    [Fact]
    public async Task ALockedVaultWillNotHandOutItsRecords()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        store.Lock();

        Assert.Throws<InvalidOperationException>(() => store.Document);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.UpdateAsync(document => document));
    }

    [Fact]
    public async Task TheRecoveryCodeOpensAVaultWhoseDeviceKeyIsGone()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        var recoveryCode = await store.CreateAsync(keys, "device-a", Kdf);
        await store.UpdateAsync((document, now) => document.WithHost(AHost(), now));
        store.Lock();

        keys.SimulateBiometricEnrolmentChange();

        var error = await Assert.ThrowsAsync<DeviceKeyUnavailableException>(
            async () => await store.UnlockWithDeviceKeyAsync(keys));
        Assert.True(error.RequiresRecovery);

        await store.UnlockWithRecoveryCodeAsync(recoveryCode);

        // The point of the whole exercise: the hosts are still there.
        Assert.Single(store.Document.Hosts);
        Assert.Equal(2, storage.WriteCount);
    }

    [Fact]
    public async Task AWrongRecoveryCodeDoesNotOpenTheVault()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        store.Lock();

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            async () => await store.UnlockWithRecoveryCodeAsync(RecoveryCode.Generate()));
        Assert.False(store.IsUnlocked);
    }

    [Fact]
    public async Task AFailedBiometricPromptLeavesTheVaultShut()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        store.Lock();

        keys.AuthenticationFails = true;

        var error = await Assert.ThrowsAsync<DeviceKeyUnavailableException>(
            async () => await store.UnlockWithDeviceKeyAsync(keys));
        Assert.Equal(DeviceKeyUnavailableReason.AuthenticationFailed, error.Reason);
        Assert.False(store.IsUnlocked);
    }

    [Fact]
    public async Task RecoveringGivesBiometricUnlockBack()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        var recoveryCode = await store.CreateAsync(keys, "device-a", Kdf);
        store.Lock();
        keys.SimulateBiometricEnrolmentChange();
        await store.UnlockWithRecoveryCodeAsync(recoveryCode);

        await store.RebindDeviceKeyAsync(keys);
        store.Lock();

        // Without this the user would type the code every time from then on,
        // which is not a recovery so much as a demotion.
        await store.UnlockWithDeviceKeyAsync(keys);
        Assert.True(store.IsUnlocked);
        Assert.Equal(2, keys.KeysCreated);
    }

    [Fact]
    public async Task RotatingTheRecoveryCodeRetiresTheOldOne()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        var original = await store.CreateAsync(keys, "device-a", Kdf);

        var replacement = await store.RotateRecoveryCodeAsync(Kdf);
        store.Lock();

        Assert.NotEqual(original, replacement);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            async () => await store.UnlockWithRecoveryCodeAsync(original));
        await store.UnlockWithRecoveryCodeAsync(replacement);
        Assert.True(store.IsUnlocked);
    }

    [Fact]
    public async Task EveryChangeIsOnDiskBeforeItIsVisible()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);

        await store.UpdateAsync((document, now) => document.WithHost(AHost("a"), now));
        await store.UpdateAsync((document, now) => document.WithHost(AHost("b"), now));

        // A host added and then lost to the app being killed is worse than the
        // few milliseconds a save costs.
        Assert.Equal(3, storage.WriteCount);
        Assert.Equal(3, store.Document.Revision);
    }

    [Fact]
    public async Task ADeletedCredentialsSecretIsGoneFromTheFileImmediately()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);

        var credential = new CredentialRecord
        {
            Id = Guid.NewGuid(),
            Label = "deploy key",
            Kind = CredentialKind.Password,
            Secret = Encoding.UTF8.GetBytes("correct-horse-battery-staple"),
        };
        await store.UpdateAsync((document, now) => document.WithCredential(credential, now));
        await store.UpdateAsync((document, now) => document.WithoutCredential(credential.Id, now));

        var tombstone = Assert.Single(store.Document.Credentials);
        Assert.True(tombstone.IsDeleted);
        Assert.Empty(tombstone.Secret);

        // Not just absent from the object graph: absent from the bytes. A deleted
        // password still readable on disk is the whole problem with deletion.
        var bytes = await storage.ReadAsync();
        Assert.DoesNotContain("correct-horse"u8.ToArray(), Windows(bytes!, 13));
    }

    [Fact]
    public async Task DeletingAHostLeavesATombstoneRatherThanAGap()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        var host = AHost();
        await store.UpdateAsync((document, now) => document.WithHost(host, now));
        await store.UpdateAsync((document, now) => document.WithoutHost(host.Id, now));

        // A row that simply vanished is indistinguishable from one that never
        // synced, so a sync would helpfully bring it back.
        var remaining = Assert.Single(store.Document.Hosts);
        Assert.True(remaining.IsDeleted);
        Assert.Empty(store.Document.LiveHosts);
    }

    [Fact]
    public async Task EachEditStampsTheDeviceAndBumpsTheRowsRevision()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        var host = AHost();

        await store.UpdateAsync((document, now) => document.WithHost(host, now));
        await store.UpdateAsync((document, now) => document.WithHost(host with { Label = "renamed" }, now));

        var saved = Assert.Single(store.Document.Hosts);
        Assert.Equal("renamed", saved.Label);
        Assert.Equal(2, saved.Revision);
        Assert.Equal("device-a", saved.OriginDeviceId);
        Assert.Equal(Now, saved.UpdatedAt);
    }

    [Fact]
    public async Task ACallerCannotSetItsOwnRevision()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);

        // Two rows claiming the same revision is the one case sync cannot
        // resolve, so the number is assigned in exactly one place.
        await store.UpdateAsync((document, now) => document.WithHost(AHost() with { Revision = 99 }, now));

        Assert.Equal(1, Assert.Single(store.Document.Hosts).Revision);
    }

    [Fact]
    public async Task TheHeaderIsReadableBeforeTheVaultIsOpened()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        store.Lock();

        var header = await store.ReadHeaderAsync();

        // This is what the lock screen runs: it has to know a vault exists, and
        // which routes in it has, before any of them can be offered.
        Assert.NotNull(header);
        Assert.Equal("device-a", header.DeviceId);
        Assert.Contains(header.WrappedKeys, k => k.Method == KeyWrapMethod.RecoveryPassphrase);
        Assert.NotEqual(0, storage.WriteCount);
    }

    [Fact]
    public async Task NoVaultReadsAsNoVaultRatherThanAnError()
    {
        var (store, _, _2) = NewStore();
        using var _3 = store;

        Assert.Null(await store.ReadHeaderAsync());
        Assert.False(await store.ExistsAsync());
    }

    [Fact]
    public async Task ACorruptedVaultIsRefusedAndLeavesNoKeyBehind()
    {
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        await store.UpdateAsync((document, now) => document.WithHost(AHost(), now));
        store.Lock();

        storage.Corrupt(offset: (await storage.ReadAsync())!.Length - 4);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            async () => await store.UnlockWithDeviceKeyAsync(keys));
        // The wrapped master key opened fine, so the failure came from the body.
        // Leaving the key ring in place would report an unlocked vault that has
        // no records in it.
        Assert.False(store.IsUnlocked);
    }

    [Fact]
    public async Task LockingZeroesTheKeyRatherThanFlippingAFlag()
    {
        var (store, _, keys) = NewStore();
        using var _2 = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        await store.UpdateAsync((document, now) => document.WithHost(AHost(), now));

        store.Lock();

        Assert.False(store.IsUnlocked);
        Assert.Throws<InvalidOperationException>(() => store.Document);
    }

    [Fact]
    public void TwoInstallsGetDifferentDeviceIds()
    {
        var first = VaultStore.DeriveDeviceId(VaultStore.NewInstallSeed());
        var second = VaultStore.DeriveDeviceId(VaultStore.NewInstallSeed());

        Assert.NotEqual(first, second);
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void TheSameSeedAlwaysGivesTheSameDeviceId()
    {
        var seed = VaultStore.NewInstallSeed();

        // Sync attributes edits to it, so a device whose id changed across a
        // restart would look like a second device fighting with itself.
        Assert.Equal(VaultStore.DeriveDeviceId(seed), VaultStore.DeriveDeviceId(seed));
    }

    [Fact]
    public async Task MergingAnotherVaultsFileIsRefusedAndWritesNothing()
    {
        var (mine, myStorage, myKeys) = NewStore();
        var (other, otherStorage, otherKeys) = NewStore();
        using var _ = mine;
        using var __ = other;
        await mine.CreateAsync(myKeys, "device-a", Kdf);
        await other.CreateAsync(otherKeys, "device-b", Kdf);
        await other.UpdateAsync((d, now) => d.WithHost(AHost("not-mine"), now));
        var before = await myStorage.ReadAsync();

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await mine.MergeAsync((await otherStorage.ReadAsync())!, "device-a"));

        Assert.Equal(before, await myStorage.ReadAsync());
        Assert.Empty(mine.Document.Hosts);
    }

    [Fact]
    public async Task MergingACopyWithNothingNewNeitherSavesNorAnnouncesAChange()
    {
        // Sync listens for Changed to know there is something to push; a merge
        // that changed nothing must not set that off.
        var (store, storage, keys) = NewStore();
        using var _ = store;
        await store.CreateAsync(keys, "device-a", Kdf);
        await store.UpdateAsync((d, now) => d.WithHost(AHost(), now));
        var copy = (await storage.ReadAsync())!;
        var changes = 0;
        store.Changed += (_, _) => changes++;

        var merged = await store.MergeAsync(copy, "device-a");

        Assert.False(merged);
        Assert.Equal(0, changes);
        Assert.Equal(copy, await storage.ReadAsync());
    }

    private static IEnumerable<byte[]> Windows(byte[] haystack, int size)
    {
        for (var i = 0; i + size <= haystack.Length; i++) yield return haystack[i..(i + size)];
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
