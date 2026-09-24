using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Security;

/// <summary>
/// The app half of MeowSSHAPI's CloudBackupLocator: which cloud backup is
/// "mine", derived from the vault's recovery code rather than any account.
/// </summary>
/// <remarks>
/// <para>
/// The recovery code already carries 160 bits of entropy, so HKDF-SHA256 is
/// enough -- there is nothing for a slow KDF to stretch. The secret proves
/// ownership to the server; the locator (SHA-256 over a label and the secret)
/// is what goes in the URL and is worthless on its own. Both constructions
/// must match the server byte for byte; CloudBackupCredentialTests and the
/// server's CloudBackupLocatorTests share known-answer vectors for exactly
/// that reason.
/// </para>
/// <para>
/// A new phone finds its backup from nothing but the recovery code, which is
/// the only thing the user carries between devices anyway -- and without
/// which a backup could not be decrypted, so tying "find it" to the same
/// secret adds no new way to lose one.
/// </para>
/// </remarks>
public sealed record CloudBackupCredential(string Locator, string SecretBase64Url, string VaultFingerprint)
{
    private const int SecretBytes = 32;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("meowssh-cloud-backup-v1");
    private static readonly byte[] AuthInfo = Encoding.UTF8.GetBytes("auth");
    private static readonly byte[] LocatorLabel = Encoding.UTF8.GetBytes("meowssh-cloud-backup-locator-v1");

    /// <summary>
    /// Unbound (empty <see cref="VaultFingerprint"/>) -- enough to find and
    /// download a backup. Bind it with <see cref="BoundTo"/> before storing it
    /// as this phone's upload identity.
    /// </summary>
    /// <exception cref="FormatException">The code is not a well-formed recovery code.</exception>
    public static CloudBackupCredential FromRecoveryCode(string recoveryCode)
    {
        if (!RecoveryCode.IsWellFormed(recoveryCode))
            throw new FormatException("That recovery code is not complete. Check for a missing character.");

        using var entropy = RecoveryCode.Parse(recoveryCode);
        var ikm = entropy.ReadOnlySpan.ToArray();
        var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, SecretBytes, Salt, AuthInfo);
        CryptographicOperations.ZeroMemory(ikm);
        try
        {
            return new CloudBackupCredential(LocatorFor(secret), Base64Url(secret), VaultFingerprint: "");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Ties the credential to one vault (see <see cref="FingerprintOf"/>).</summary>
    public CloudBackupCredential BoundTo(string vaultFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFingerprint);
        return this with { VaultFingerprint = vaultFingerprint };
    }

    public static string LocatorFor(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes)
            throw new ArgumentException($"A cloud backup secret is exactly {SecretBytes} bytes.", nameof(secret));
        var input = new byte[LocatorLabel.Length + SecretBytes];
        LocatorLabel.CopyTo(input, 0);
        secret.CopyTo(input.AsSpan(LocatorLabel.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>
    /// Identifies a vault by its recovery-wrapped master key. Restoring a
    /// different backup (locally or from the cloud) replaces that key, so a
    /// stored credential whose fingerprint no longer matches belongs to a
    /// vault that is no longer on this phone -- and must not keep uploading
    /// this vault under that old identity.
    /// </summary>
    /// <exception cref="VaultFormatException">The vault has no recovery-code key.</exception>
    public static string FingerprintOf(VaultHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var recovery = header.WrappedKeys.FirstOrDefault(static key => key.Method == KeyWrapMethod.RecoveryPassphrase)
            ?? throw new VaultFormatException("This vault has no recovery-code key, so it cannot be backed up to the cloud.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(recovery.Payload);
        if (recovery.Salt is not null) hash.AppendData(recovery.Salt);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
