using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Storage;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

public class StoredVaultSessionTests
{
    private sealed record Harness(
        StoredVaultSession Session, VaultStore Store, InMemoryVaultStorage Storage,
        FakeDeviceKeyStore Keys, FakeBiometricGate Biometrics);

    private static Harness NewSession(InMemoryVaultStorage? storage = null, FakeDeviceKeyStore? keys = null)
    {
        storage ??= new InMemoryVaultStorage();
        keys ??= new FakeDeviceKeyStore();
        var biometrics = new FakeBiometricGate();
        var store = new VaultStore(storage);
        return new Harness(
            // Cheap Argon2id parameters: these tests stretch a passphrase dozens
            // of times, and the cost of the real ones is the point of them.
            new StoredVaultSession(
                store, keys, biometrics, new FakeDeviceIdentity(),
                new Argon2idKeyDerivation(KdfParameters.Testing)),
            store, storage, keys, biometrics);
    }

    [Fact]
    public async Task AFreshInstallReportsThatThereIsNoVaultYet()
    {
        var h = NewSession();
        using var _ = h.Store;

        await h.Session.InitializeAsync();

        // Showing a returning user's unlock screen on a first run offers a
        // fingerprint that cannot possibly work.
        Assert.Equal(VaultState.NotCreated, h.Session.State);
    }

    [Fact]
    public async Task SetupCreatesTheVaultOpenAndHandsBackTheCodeOnce()
    {
        var h = NewSession();
        using var _ = h.Store;

        var result = await h.Session.CreateAsync();

        Assert.True(result.Succeeded);
        Assert.True(RecoveryCode.IsWellFormed(result.RecoveryCode));
        Assert.Equal(VaultState.Unlocked, h.Session.State);
        Assert.True(h.Store.IsUnlocked);
    }

    [Fact]
    public async Task SetupAsksForBiometricsBeforeItCreatesAnything()
    {
        var h = NewSession();
        using var _ = h.Store;
        h.Biometrics.Result = BiometricResult.Cancelled;

        var result = await h.Session.CreateAsync();

        // Creating the vault first and then finding the sensor unusable would
        // leave a vault only the recovery code could ever open.
        Assert.False(result.Succeeded);
        Assert.Equal(0, h.Storage.WriteCount);
        Assert.Equal(VaultState.NotCreated, h.Session.State);
    }

    [Fact]
    public async Task SetupWithNoFingerprintEnrolledSaysWhatToDoAboutIt()
    {
        var h = NewSession();
        using var _ = h.Store;
        h.Biometrics.Availability = BiometricAvailability.NotEnrolled;

        var result = await h.Session.CreateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Set up a fingerprint", result.Message, StringComparison.Ordinal);
        Assert.Empty(h.Biometrics.Prompts);
    }

    [Fact]
    public async Task SettingUpTwiceIsRefused()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();

        var again = await h.Session.CreateAsync();

        Assert.False(again.Succeeded);
        Assert.Contains("already exists", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReturningUserSeesALockedVaultAndUnlocksIt()
    {
        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();

        var first = NewSession(storage, keys);
        await first.Session.CreateAsync();
        first.Store.Dispose();

        var second = NewSession(storage, keys);
        using var _ = second.Store;
        await second.Session.InitializeAsync();
        Assert.Equal(VaultState.Locked, second.Session.State);

        var result = await second.Session.UnlockAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(VaultState.Unlocked, second.Session.State);
    }

    [Fact]
    public async Task OnlyOnePromptIsRaisedPerUnlock()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();
        h.Biometrics.Prompts.Clear();

        await h.Session.UnlockAsync();

        // The Keystore would raise its own if asked; two prompts for one unlock
        // is confusing, and the app's wording is better than the platform default.
        var prompt = Assert.Single(h.Biometrics.Prompts);
        Assert.Equal("Unlock MeowSSH", prompt.Title);
    }

    [Fact]
    public async Task ACancelledPromptLeavesTheVaultLocked()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();
        h.Biometrics.Result = BiometricResult.Cancelled;

        var result = await h.Session.UnlockAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Unlock cancelled.", result.Message);
        Assert.Equal(VaultState.Locked, h.Session.State);
        Assert.False(h.Store.IsUnlocked);
    }

    [Fact]
    public async Task BeingLockedOutPointsAtTheRecoveryCode()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();
        h.Biometrics.Result = BiometricResult.LockedOut;

        var result = await h.Session.UnlockAsync();

        Assert.Contains("recovery code", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetiredDeviceKeyMovesTheSessionToRecoveryRatherThanFailing()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();

        h.Keys.SimulateBiometricEnrolmentChange();
        var result = await h.Session.UnlockAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(VaultState.NeedsRecovery, result.State);
        Assert.Equal(VaultState.NeedsRecovery, h.Session.State);
        Assert.Contains("biometric enrolment changed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestartAfterABiometricChangeGoesStraightToRecovery()
    {
        var storage = new InMemoryVaultStorage();
        var keys = new FakeDeviceKeyStore();

        var first = NewSession(storage, keys);
        await first.Session.CreateAsync();
        first.Store.Dispose();

        keys.IsUnavailable = true;

        var second = NewSession(storage, keys);
        using var _ = second.Store;
        await second.Session.InitializeAsync();

        // Offering a fingerprint that cannot work, and only then explaining, is
        // one wasted attempt at the exact moment the user is already worried.
        Assert.Equal(VaultState.NeedsRecovery, second.Session.State);
    }

    [Fact]
    public async Task TheRecoveryCodeOpensTheVaultAndRestoresBiometricUnlock()
    {
        var h = NewSession();
        using var _ = h.Store;
        var setup = await h.Session.CreateAsync();
        await h.Session.LockAsync();
        h.Keys.SimulateBiometricEnrolmentChange();

        var recovered = await h.Session.UnlockWithRecoveryCodeAsync(setup.RecoveryCode!);
        Assert.True(recovered.Succeeded);

        await h.Session.LockAsync();
        var afterwards = await h.Session.UnlockAsync();

        Assert.True(afterwards.Succeeded);
        Assert.Equal(2, h.Keys.KeysCreated);
    }

    [Fact]
    public async Task RecoveryStillSucceedsWhenTheDeviceKeyCannotBeRebuilt()
    {
        var h = NewSession();
        using var _ = h.Store;
        var setup = await h.Session.CreateAsync();
        await h.Session.LockAsync();
        h.Keys.IsUnavailable = true;

        var result = await h.Session.UnlockWithRecoveryCodeAsync(setup.RecoveryCode!);

        // Getting back in is what the user asked for. Failing the recovery
        // because the optional half did not work would be absurd.
        Assert.True(result.Succeeded);
        Assert.Equal(VaultState.Unlocked, h.Session.State);
    }

    [Fact]
    public async Task AWrongRecoveryCodeSaysSoWithoutMentioningCryptography()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();

        var result = await h.Session.UnlockWithRecoveryCodeAsync(RecoveryCode.Generate());

        Assert.False(result.Succeeded);
        Assert.Equal("That recovery code does not match this vault.", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("ABCD-EFGH-JKMN")]
    public async Task AnIncompleteRecoveryCodeIsCalledIncomplete(string code)
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();
        await h.Session.LockAsync();

        var result = await h.Session.UnlockWithRecoveryCodeAsync(code);

        // "Does not match" would send the user looking for the wrong code when
        // the real problem is a character they missed typing.
        Assert.Contains("not complete", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockingZeroesTheKeyRatherThanHidingTheScreen()
    {
        var h = NewSession();
        using var _ = h.Store;
        await h.Session.CreateAsync();

        await h.Session.LockAsync();

        Assert.Equal(VaultState.Locked, h.Session.State);
        Assert.False(h.Store.IsUnlocked);
        Assert.Throws<InvalidOperationException>(() => h.Store.Document);
    }

    [Fact]
    public async Task StateChangesAreAnnouncedOnceEach()
    {
        var h = NewSession();
        using var _ = h.Store;
        var changes = 0;
        h.Session.StateChanged += (_, _) => changes++;

        await h.Session.CreateAsync();   // NotCreated -> Unlocked
        await h.Session.LockAsync();     // Unlocked   -> Locked
        await h.Session.LockAsync();     // already locked; nothing to announce
        await h.Session.UnlockAsync();   // Locked     -> Unlocked

        // A screen that redraws on every no-op transition flickers, and a lock
        // that fires twice is the kind of thing that hides a real double-unlock.
        Assert.Equal(3, changes);
    }
}
