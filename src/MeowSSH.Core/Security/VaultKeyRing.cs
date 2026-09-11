using System.Security.Cryptography;

namespace MeowSSH.Core.Security;

/// <summary>What a key derived from the vault master key is allowed to do.</summary>
public enum VaultKeyPurpose
{
    /// <summary>Sealing the secret fields of vault records.</summary>
    Secrets,

    /// <summary>Sealing an exported backup file.</summary>
    Backup,

    /// <summary>Deriving blind index tokens for searching without decrypting.</summary>
    SearchIndex,
}

public enum KeyWrapMethod
{
    /// <summary>Wrapped by a non-exportable hardware key, gated on biometrics.</summary>
    DeviceKeyStore = 1,

    /// <summary>Wrapped by a key stretched from the user's recovery passphrase.</summary>
    RecoveryPassphrase = 2,
}

/// <summary>
/// One encrypted copy of the vault master key. A vault keeps several, so that
/// losing any single unlock route does not lose the data.
/// </summary>
public sealed record WrappedVaultKey(
    string Id,
    KeyWrapMethod Method,
    byte[] Payload,
    KdfParameters? Kdf = null,
    byte[]? Salt = null);

/// <summary>
/// Holds the vault master key in memory and derives the separate keys used for
/// each purpose.
/// </summary>
/// <remarks>
/// <para>
/// The master key is never used to encrypt anything directly. Every use goes
/// through an HKDF-derived subkey labelled with its purpose, so a weakness or a
/// leak in one area — a backup file handed to the wrong person, say — cannot be
/// turned into the key that opens stored credentials.
/// </para>
/// <para>
/// The master key itself is only ever stored wrapped, in as many copies as there
/// are ways to unlock the vault. This is why an account system can never be the
/// thing that holds the key: adding a login later means adding another wrapped
/// copy, not moving the key somewhere a server can see it.
/// </para>
/// </remarks>
public sealed class VaultKeyRing : IDisposable
{
    private const string HkdfPrefix = "meowssh:v1:";
    private readonly SecretBuffer _masterKey;
    private bool _disposed;

    private VaultKeyRing(SecretBuffer masterKey) => _masterKey = masterKey;

    /// <summary>Generates a brand new vault master key.</summary>
    public static VaultKeyRing CreateNew() => new(SecretBuffer.Random(VaultCrypto.KeySize));

    /// <summary>Adopts an already-unwrapped master key. Takes ownership of it.</summary>
    public static VaultKeyRing FromMasterKey(SecretBuffer masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length != VaultCrypto.KeySize)
            throw new ArgumentException($"A vault master key is {VaultCrypto.KeySize} bytes.", nameof(masterKey));
        return new VaultKeyRing(masterKey);
    }

    /// <summary>
    /// Derives the key used for <paramref name="purpose"/>. The caller owns the
    /// returned buffer and should dispose it as soon as the operation is done.
    /// </summary>
    public SecretBuffer DerivePurposeKey(VaultKeyPurpose purpose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = System.Text.Encoding.UTF8.GetBytes(HkdfPrefix + purpose.ToString().ToLowerInvariant());
        var derived = SecretBuffer.Allocate(VaultCrypto.KeySize);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey.ReadOnlySpan, derived.Span, salt: default, info);
        return derived;
    }

    /// <summary>Produces an encrypted copy of the master key, unlockable with <paramref name="passphrase"/>.</summary>
    public WrappedVaultKey WrapWithPassphrase(string id, ReadOnlySpan<byte> passphrase, IKeyDerivation kdf)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(kdf);

        var salt = RandomNumberGenerator.GetBytes(Argon2idKeyDerivation.SaltSize);
        using var wrappingKey = kdf.DeriveKey(passphrase, salt, VaultCrypto.KeySize);
        var payload = VaultCrypto.Seal(wrappingKey.ReadOnlySpan, _masterKey.ReadOnlySpan, WrapContext(id, KeyWrapMethod.RecoveryPassphrase));
        return new WrappedVaultKey(id, KeyWrapMethod.RecoveryPassphrase, payload, kdf.Parameters, salt);
    }

    /// <summary>Reopens a vault from a passphrase-wrapped master key.</summary>
    /// <exception cref="CryptographicException">The passphrase is wrong, or the wrapped key has been tampered with.</exception>
    public static VaultKeyRing UnwrapWithPassphrase(WrappedVaultKey wrapped, ReadOnlySpan<byte> passphrase)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.Method != KeyWrapMethod.RecoveryPassphrase)
            throw new ArgumentException($"This wrapped key is unlocked by {wrapped.Method}, not a passphrase.", nameof(wrapped));
        if (wrapped.Kdf is null || wrapped.Salt is null)
            throw new ArgumentException("A passphrase-wrapped key must carry its derivation parameters and salt.", nameof(wrapped));

        var kdf = new Argon2idKeyDerivation(wrapped.Kdf);
        using var wrappingKey = kdf.DeriveKey(passphrase, wrapped.Salt, VaultCrypto.KeySize);
        var masterKey = VaultCrypto.Open(wrappingKey.ReadOnlySpan, wrapped.Payload, WrapContext(wrapped.Id, KeyWrapMethod.RecoveryPassphrase));
        return FromMasterKey(masterKey);
    }

    /// <summary>Produces a copy of the master key wrapped by the device's hardware key store.</summary>
    public async ValueTask<WrappedVaultKey> WrapWithDeviceKeyStoreAsync(
        string id, IDeviceKeyStore keyStore, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(keyStore);

        var payload = await keyStore.WrapAsync(_masterKey.ReadOnlySpan.ToArray(), cancellationToken).ConfigureAwait(false);
        return new WrappedVaultKey(id, KeyWrapMethod.DeviceKeyStore, payload);
    }

    /// <summary>Reopens a vault from a device-wrapped master key, prompting for biometrics if the key requires it.</summary>
    public static async ValueTask<VaultKeyRing> UnwrapWithDeviceKeyStoreAsync(
        WrappedVaultKey wrapped, IDeviceKeyStore keyStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(keyStore);
        if (wrapped.Method != KeyWrapMethod.DeviceKeyStore)
            throw new ArgumentException($"This wrapped key is unlocked by {wrapped.Method}, not the device key store.", nameof(wrapped));

        var masterKey = await keyStore.UnwrapAsync(wrapped.Payload, cancellationToken).ConfigureAwait(false);
        return FromMasterKey(masterKey);
    }

    /// <summary>
    /// Copies the raw master key out of the ring, for the one caller that has to
    /// put it somewhere else: <see cref="VaultBackup"/>, which seals it into an
    /// export file so the vault can be restored onto another device.
    /// </summary>
    /// <remarks>
    /// Internal on purpose. Everything else derives a purpose key instead, so
    /// this is the only place in the codebase where the master key leaves.
    /// </remarks>
    internal SecretBuffer ExportMasterKeyForBackup()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SecretBuffer.CopyFrom(_masterKey.ReadOnlySpan);
    }

    private static byte[] WrapContext(string id, KeyWrapMethod method) =>
        VaultCrypto.RecordContext("vaultkey." + method.ToString().ToLowerInvariant(), id, schemaVersion: 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _masterKey.Dispose();
    }
}
