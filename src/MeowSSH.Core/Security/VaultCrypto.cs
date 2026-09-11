using System.Security.Cryptography;
using System.Text;

namespace MeowSSH.Core.Security;

/// <summary>
/// Authenticated encryption for everything the vault stores at rest:
/// AES-256-GCM with a fresh random nonce per message, and associated data
/// binding each ciphertext to the record it belongs to.
/// </summary>
/// <remarks>
/// The associated data is what stops a record from being moved. Without it, an
/// attacker with write access to the database could copy the encrypted password
/// of a host you trust over the record for a host they control, and the app
/// would decrypt it happily. Binding the record's type, id and schema version
/// into the tag makes that swap fail to authenticate.
/// </remarks>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Bytes of overhead a sealed message carries over its plaintext.</summary>
    public const int Overhead = NonceSize + TagSize;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/>, returning <c>nonce || ciphertext || tag</c>.
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        CheckKey(key);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        var ciphertext = output.AsSpan(NonceSize, plaintext.Length);
        var tag = output.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>
    /// Decrypts a <c>nonce || ciphertext || tag</c> message produced by <see cref="Seal"/>.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The message was truncated, corrupted, encrypted under a different key, or
    /// carries associated data that does not match — including a record moved
    /// from somewhere else in the vault.
    /// </exception>
    public static SecretBuffer Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedMessage, ReadOnlySpan<byte> associatedData)
    {
        CheckKey(key);
        if (sealedMessage.Length < Overhead)
            throw new CryptographicException("Sealed message is shorter than its own framing; it is truncated or not a sealed message.");

        var nonce = sealedMessage[..NonceSize];
        var ciphertext = sealedMessage[NonceSize..^TagSize];
        var tag = sealedMessage[^TagSize..];

        var plaintext = SecretBuffer.Allocate(ciphertext.Length);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext.Span, associatedData);
            return plaintext;
        }
        catch
        {
            plaintext.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the associated data that binds a sealed value to its own record.
    /// </summary>
    public static byte[] RecordContext(string recordType, string recordId, int schemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordType);
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        // Length-prefixed rather than delimiter-joined: concatenating with a
        // separator lets ("ab", "c") and ("a", "bc") produce the same context,
        // which would let those two records be swapped.
        var writer = new MemoryStream();
        WriteField(writer, recordType);
        WriteField(writer, recordId);
        Span<byte> version = stackalloc byte[4];
        BitConverter.TryWriteBytes(version, schemaVersion);
        writer.Write(version);
        return writer.ToArray();

        static void WriteField(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BitConverter.TryWriteBytes(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
    }

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Vault keys are {KeySize} bytes; got {key.Length}.", nameof(key));
    }
}
