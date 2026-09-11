using System.Security.Cryptography;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class VaultBackupTests
{
    private static readonly IKeyDerivation FastKdf = new Argon2idKeyDerivation(KdfParameters.Testing);
    private static ReadOnlySpan<byte> Passphrase => "correct horse battery staple"u8;

    [Fact]
    public void RestoredVaultCanStillOpenRecordsSealedBeforeTheBackup()
    {
        // The whole point of a restore: records exported from the old device must
        // decrypt on the new one, which means the master key has to survive.
        using var original = VaultKeyRing.CreateNew();
        var context = VaultCrypto.RecordContext("credential", "prod-db", 1);
        byte[] sealedCredential;
        using (var secrets = original.DerivePurposeKey(VaultKeyPurpose.Secrets))
        {
            sealedCredential = VaultCrypto.Seal(secrets.ReadOnlySpan, "prod-password"u8, context);
        }

        var file = VaultBackup.Export(original, "vault-records"u8, Passphrase, FastKdf);

        using var restored = VaultBackup.Import(file, Passphrase);
        using var restoredSecrets = restored.KeyRing.DerivePurposeKey(VaultKeyPurpose.Secrets);
        using var recovered = VaultCrypto.Open(restoredSecrets.ReadOnlySpan, sealedCredential, context);

        Assert.Equal("prod-password"u8.ToArray(), recovered.ReadOnlySpan.ToArray());
        Assert.Equal("vault-records"u8.ToArray(), restored.Payload.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void WrongPassphraseDoesNotOpenTheBackup()
    {
        using var ring = VaultKeyRing.CreateNew();
        var file = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => VaultBackup.Import(file, "wrong passphrase"u8));
    }

    [Fact]
    public void DowngradingTheCostParametersInTheHeaderBreaksTheFile()
    {
        // An attacker who could rewrite the stored Argon2 cost down to something
        // trivial would make the file cheap to brute-force. The header is the
        // AEAD's associated data precisely so that edit cannot go unnoticed.
        using var ring = VaultKeyRing.CreateNew();
        var file = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);

        var before = VaultBackup.Inspect(file);
        // Memory cost sits after magic (8) + version (2) + algorithm (1).
        file[11] = 0; file[12] = 0; file[13] = 0; file[14] = 8;
        var after = VaultBackup.Inspect(file);

        Assert.NotEqual(before.Kdf.MemoryKiB, after.Kdf.MemoryKiB);
        Assert.ThrowsAny<CryptographicException>(() => VaultBackup.Import(file, Passphrase));
    }

    [Fact]
    public void TamperingWithTheCiphertextIsDetected()
    {
        using var ring = VaultKeyRing.CreateNew();
        var file = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);

        file[^1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultBackup.Import(file, Passphrase));
    }

    [Fact]
    public void InspectReportsTheHeaderWithoutThePassphrase()
    {
        using var ring = VaultKeyRing.CreateNew();
        var payload = new byte[512];
        var file = VaultBackup.Export(ring, payload, Passphrase, FastKdf);

        var info = VaultBackup.Inspect(file);

        Assert.Equal(VaultBackup.CurrentFormatVersion, info.FormatVersion);
        Assert.Equal(KdfParameters.Testing, info.Kdf);
        Assert.Equal(VaultCrypto.KeySize + payload.Length, info.PayloadLength);
    }

    [Fact]
    public void ANonBackupFileIsRejectedClearly()
    {
        var notABackup = "this is a photo, not a vault"u8.ToArray();

        var ex = Assert.Throws<InvalidBackupFileException>(() => VaultBackup.Inspect(notABackup));
        Assert.Contains("not a MeowSSH backup", ex.Message);
    }

    [Fact]
    public void AFileFromANewerFormatTellsTheUserToUpdate()
    {
        using var ring = VaultKeyRing.CreateNew();
        var file = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);
        file[9] = VaultBackup.CurrentFormatVersion + 1;

        var ex = Assert.Throws<InvalidBackupFileException>(() => VaultBackup.Inspect(file));
        Assert.Contains("Update the app", ex.Message);
    }

    [Fact]
    public void ATruncatedBackupIsRejected()
    {
        using var ring = VaultKeyRing.CreateNew();
        var file = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);

        Assert.Throws<InvalidBackupFileException>(() => VaultBackup.Inspect(file.AsSpan(0, 20)));
    }

    [Fact]
    public void ExportingTwiceProducesDifferentFilesForTheSameVault()
    {
        using var ring = VaultKeyRing.CreateNew();

        var first = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);
        var second = VaultBackup.Export(ring, "records"u8, Passphrase, FastKdf);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AnEmptyPassphraseIsRefused()
    {
        using var ring = VaultKeyRing.CreateNew();

        Assert.Throws<ArgumentException>(
            () => VaultBackup.Export(ring, "records"u8, ReadOnlySpan<byte>.Empty, FastKdf));
    }

    [Fact]
    public void ARecoveryCodeWorksAsTheBackupPassphrase()
    {
        using var ring = VaultKeyRing.CreateNew();
        var code = RecoveryCode.Generate();

        byte[] file;
        using (var entropy = RecoveryCode.Parse(code))
        {
            file = VaultBackup.Export(ring, "records"u8, entropy.ReadOnlySpan, FastKdf);
        }

        // Typed back in the way a user would: lower case, spaces for dashes.
        using var retyped = RecoveryCode.Parse(code.Replace('-', ' ').ToLowerInvariant());
        using var restored = VaultBackup.Import(file, retyped.ReadOnlySpan);

        Assert.Equal("records"u8.ToArray(), restored.Payload.ReadOnlySpan.ToArray());
    }
}
