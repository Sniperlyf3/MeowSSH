using System.Security.Cryptography;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class VaultKeyRingTests
{
    private static readonly IKeyDerivation FastKdf = new Argon2idKeyDerivation(KdfParameters.Testing);

    [Fact]
    public void PurposeKeysAreDeterministicPerRingAndDifferentPerPurpose()
    {
        using var ring = VaultKeyRing.CreateNew();

        using var secrets = ring.DerivePurposeKey(VaultKeyPurpose.Secrets);
        using var secretsAgain = ring.DerivePurposeKey(VaultKeyPurpose.Secrets);
        using var backup = ring.DerivePurposeKey(VaultKeyPurpose.Backup);
        using var index = ring.DerivePurposeKey(VaultKeyPurpose.SearchIndex);

        Assert.Equal(secrets.ReadOnlySpan.ToArray(), secretsAgain.ReadOnlySpan.ToArray());
        Assert.NotEqual(secrets.ReadOnlySpan.ToArray(), backup.ReadOnlySpan.ToArray());
        Assert.NotEqual(secrets.ReadOnlySpan.ToArray(), index.ReadOnlySpan.ToArray());
        Assert.NotEqual(backup.ReadOnlySpan.ToArray(), index.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void ABackupKeyLeakDoesNotRevealTheSecretsKey()
    {
        // The reason purposes are separated at all: handing someone a backup file
        // key must not hand them the key that opens stored credentials.
        using var ring = VaultKeyRing.CreateNew();
        using var backup = ring.DerivePurposeKey(VaultKeyPurpose.Backup);
        using var secrets = ring.DerivePurposeKey(VaultKeyPurpose.Secrets);

        var context = VaultCrypto.RecordContext("credential", "prod", 1);
        var sealedSecret = VaultCrypto.Seal(secrets.ReadOnlySpan, "prod-password"u8, context);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => VaultCrypto.Open(backup.ReadOnlySpan, sealedSecret, context));
    }

    [Fact]
    public void TwoRingsHaveDifferentKeys()
    {
        using var first = VaultKeyRing.CreateNew();
        using var second = VaultKeyRing.CreateNew();

        using var a = first.DerivePurposeKey(VaultKeyPurpose.Secrets);
        using var b = second.DerivePurposeKey(VaultKeyPurpose.Secrets);

        Assert.NotEqual(a.ReadOnlySpan.ToArray(), b.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void PassphraseWrappedKeyRoundTripsToTheSameVault()
    {
        using var original = VaultKeyRing.CreateNew();
        using var expected = original.DerivePurposeKey(VaultKeyPurpose.Secrets);

        var wrapped = original.WrapWithPassphrase("recovery", "correct horse battery staple"u8, FastKdf);
        using var reopened = VaultKeyRing.UnwrapWithPassphrase(wrapped, "correct horse battery staple"u8);
        using var actual = reopened.DerivePurposeKey(VaultKeyPurpose.Secrets);

        Assert.Equal(expected.ReadOnlySpan.ToArray(), actual.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void WrongPassphraseDoesNotUnwrap()
    {
        using var ring = VaultKeyRing.CreateNew();
        var wrapped = ring.WrapWithPassphrase("recovery", "correct horse battery staple"u8, FastKdf);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => VaultKeyRing.UnwrapWithPassphrase(wrapped, "correct horse battery stapler"u8));
    }

    [Fact]
    public void WrappingTheSameKeyTwiceProducesDifferentSaltsAndPayloads()
    {
        using var ring = VaultKeyRing.CreateNew();

        var first = ring.WrapWithPassphrase("a", "same passphrase"u8, FastKdf);
        var second = ring.WrapWithPassphrase("b", "same passphrase"u8, FastKdf);

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Payload, second.Payload);
    }

    [Fact]
    public void AWrappedKeyCannotBeRelabelledToUnlockADifferentSlot()
    {
        // The wrap id is bound into the tag, so moving a wrapped key into another
        // slot in the database has to fail rather than silently succeed.
        using var ring = VaultKeyRing.CreateNew();
        var wrapped = ring.WrapWithPassphrase("recovery", "passphrase"u8, FastKdf);
        var relabelled = wrapped with { Id = "device-fallback" };

        Assert.Throws<AuthenticationTagMismatchException>(
            () => VaultKeyRing.UnwrapWithPassphrase(relabelled, "passphrase"u8));
    }

    [Fact]
    public void UnwrappingRejectsAKeyWrappedByADifferentMethod()
    {
        var deviceWrapped = new WrappedVaultKey("device", KeyWrapMethod.DeviceKeyStore, [1, 2, 3]);

        var ex = Assert.Throws<ArgumentException>(
            () => VaultKeyRing.UnwrapWithPassphrase(deviceWrapped, "passphrase"u8));
        Assert.Contains("DeviceKeyStore", ex.Message);
    }

    [Fact]
    public void UnwrappingRejectsAPassphraseKeyMissingItsDerivationParameters()
    {
        var malformed = new WrappedVaultKey("recovery", KeyWrapMethod.RecoveryPassphrase, [1, 2, 3], Kdf: null, Salt: null);

        Assert.Throws<ArgumentException>(() => VaultKeyRing.UnwrapWithPassphrase(malformed, "passphrase"u8));
    }

    [Fact]
    public void DisposedRingRefusesToDeriveFurtherKeys()
    {
        var ring = VaultKeyRing.CreateNew();
        ring.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ring.DerivePurposeKey(VaultKeyPurpose.Secrets));
    }
}
