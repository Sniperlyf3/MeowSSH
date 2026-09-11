using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MeowSSH.Core.Security;

/// <summary>
/// Reads and writes <c>.meowvault</c> files: an end-to-end encrypted export of a
/// vault that only the user's recovery passphrase can open.
/// </summary>
/// <remarks>
/// <para>
/// The file carries the vault master key itself, sealed under a key stretched
/// from the passphrase. That is what makes a restore a true restore: records
/// exported from the old device are still sealed under the same master key, so
/// they decrypt on the new one. A backup that only copied the records would
/// force a re-encryption pass and could not be restored without the original
/// device.
/// </para>
/// <para>
/// Every parameter needed to reproduce the derivation is stored in the clear in
/// the header — and the whole header is the AEAD's associated data, so an
/// attacker cannot weaken the file by rewriting its cost parameters down to
/// something brute-forceable. Tampering with any header byte makes the file fail
/// to authenticate rather than open cheaply.
/// </para>
/// </remarks>
public static class VaultBackup
{
    private static ReadOnlySpan<byte> Magic => "MEOWVLT\0"u8;

    /// <summary>The format this build writes.</summary>
    public const ushort CurrentFormatVersion = 1;

    /// <summary>The file extension these exports use.</summary>
    public const string FileExtension = ".meowvault";

    /// <summary>What came back out of a backup file.</summary>
    /// <param name="KeyRing">The restored vault master key.</param>
    /// <param name="Payload">The caller's own serialized vault contents.</param>
    public sealed record Contents(VaultKeyRing KeyRing, SecretBuffer Payload) : IDisposable
    {
        public void Dispose()
        {
            KeyRing.Dispose();
            Payload.Dispose();
        }
    }

    /// <summary>
    /// Seals <paramref name="payload"/> and the vault master key into a backup file.
    /// </summary>
    /// <param name="keyRing">The vault being exported.</param>
    /// <param name="payload">Serialized vault contents, opaque to this layer.</param>
    /// <param name="passphrase">The recovery passphrase or code protecting the file.</param>
    /// <param name="kdf">Cost parameters to stretch the passphrase with.</param>
    public static byte[] Export(VaultKeyRing keyRing, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> passphrase, IKeyDerivation kdf)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(kdf);
        if (passphrase.IsEmpty)
            throw new ArgumentException("A backup passphrase is required.", nameof(passphrase));

        var salt = RandomNumberGenerator.GetBytes(Argon2idKeyDerivation.SaltSize);
        var header = BuildHeader(CurrentFormatVersion, kdf.Parameters, salt);

        using var masterKey = keyRing.ExportMasterKeyForBackup();
        using var plaintext = SecretBuffer.Allocate(VaultCrypto.KeySize + payload.Length);
        masterKey.ReadOnlySpan.CopyTo(plaintext.Span);
        payload.CopyTo(plaintext.Span[VaultCrypto.KeySize..]);

        using var fileKey = kdf.DeriveKey(passphrase, salt, VaultCrypto.KeySize);
        var sealedPayload = VaultCrypto.Seal(fileKey.ReadOnlySpan, plaintext.ReadOnlySpan, header);

        var file = new byte[header.Length + sealedPayload.Length];
        header.CopyTo(file, 0);
        sealedPayload.CopyTo(file, header.Length);
        return file;
    }

    /// <summary>Opens a backup file with the passphrase it was sealed under.</summary>
    /// <exception cref="InvalidBackupFileException">The file is not a backup, or is from a newer format.</exception>
    /// <exception cref="CryptographicException">The passphrase is wrong, or the file has been altered.</exception>
    public static Contents Import(ReadOnlySpan<byte> file, ReadOnlySpan<byte> passphrase)
    {
        var (parameters, salt, headerLength) = ParseHeader(file);
        var header = file[..headerLength].ToArray();
        var body = file[headerLength..];

        var kdf = new Argon2idKeyDerivation(parameters);
        using var fileKey = kdf.DeriveKey(passphrase, salt, VaultCrypto.KeySize);
        using var plaintext = VaultCrypto.Open(fileKey.ReadOnlySpan, body, header);

        if (plaintext.Length < VaultCrypto.KeySize)
            throw new InvalidBackupFileException("Backup file decrypted but is too short to contain a vault key.");

        var masterKey = SecretBuffer.CopyFrom(plaintext.ReadOnlySpan[..VaultCrypto.KeySize]);
        var payload = SecretBuffer.CopyFrom(plaintext.ReadOnlySpan[VaultCrypto.KeySize..]);
        return new Contents(VaultKeyRing.FromMasterKey(masterKey), payload);
    }

    /// <summary>
    /// Reads a backup's header without needing the passphrase, so the restore
    /// screen can tell the user what they picked before asking them to type it.
    /// </summary>
    public static BackupFileInfo Inspect(ReadOnlySpan<byte> file)
    {
        var (parameters, _, headerLength) = ParseHeader(file);
        return new BackupFileInfo(
            BinaryPrimitives.ReadUInt16BigEndian(file.Slice(Magic.Length, 2)),
            parameters,
            file.Length - headerLength - VaultCrypto.Overhead);
    }

    private static byte[] BuildHeader(ushort formatVersion, KdfParameters parameters, byte[] salt)
    {
        parameters.Validate();
        var header = new byte[Magic.Length + 2 + 1 + 4 + 4 + 1 + 1 + salt.Length];
        var cursor = header.AsSpan();

        Magic.CopyTo(cursor);
        cursor = cursor[Magic.Length..];
        BinaryPrimitives.WriteUInt16BigEndian(cursor, formatVersion);
        cursor = cursor[2..];
        cursor[0] = (byte)parameters.Algorithm;
        cursor = cursor[1..];
        BinaryPrimitives.WriteUInt32BigEndian(cursor, (uint)parameters.MemoryKiB);
        cursor = cursor[4..];
        BinaryPrimitives.WriteUInt32BigEndian(cursor, (uint)parameters.Iterations);
        cursor = cursor[4..];
        cursor[0] = (byte)parameters.Parallelism;
        cursor = cursor[1..];
        cursor[0] = (byte)salt.Length;
        cursor = cursor[1..];
        salt.CopyTo(cursor);
        return header;
    }

    private static (KdfParameters Parameters, byte[] Salt, int HeaderLength) ParseHeader(ReadOnlySpan<byte> file)
    {
        const int fixedLength = 8 + 2 + 1 + 4 + 4 + 1 + 1;
        if (file.Length < fixedLength)
            throw new InvalidBackupFileException("File is too short to be a MeowSSH backup.");
        if (!file[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidBackupFileException("File is not a MeowSSH backup.");

        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(Magic.Length, 2));
        if (formatVersion is 0 or > CurrentFormatVersion)
            throw new InvalidBackupFileException(
                $"This backup is format version {formatVersion}; this version of MeowSSH reads up to {CurrentFormatVersion}. Update the app and try again.");

        var cursor = file[(Magic.Length + 2)..];
        var algorithm = (KdfAlgorithm)cursor[0];
        var memoryKiB = (int)BinaryPrimitives.ReadUInt32BigEndian(cursor[1..]);
        var iterations = (int)BinaryPrimitives.ReadUInt32BigEndian(cursor[5..]);
        var parallelism = cursor[9];
        var saltLength = cursor[10];

        var headerLength = fixedLength + saltLength;
        if (file.Length < headerLength + VaultCrypto.Overhead)
            throw new InvalidBackupFileException("Backup file is truncated.");

        var parameters = new KdfParameters(algorithm, memoryKiB, iterations, parallelism);
        parameters.Validate();
        return (parameters, file.Slice(fixedLength, saltLength).ToArray(), headerLength);
    }
}

/// <param name="FormatVersion">Backup format the file was written in.</param>
/// <param name="Kdf">Cost parameters the passphrase will be stretched with.</param>
/// <param name="PayloadLength">Size of the encrypted contents, in bytes.</param>
public sealed record BackupFileInfo(ushort FormatVersion, KdfParameters Kdf, int PayloadLength);

public sealed class InvalidBackupFileException(string message) : Exception(message);
