namespace MeowSSH.Core.Model;

/// <summary>What kind of secret a credential holds.</summary>
public enum CredentialKind
{
    /// <summary>A password typed at the server's prompt.</summary>
    Password = 1,

    /// <summary>A private key in OpenSSH or PEM form, optionally passphrase-protected.</summary>
    PrivateKey = 2,

    /// <summary>An SSH public key used only to authorize access; it cannot authenticate an outbound connection.</summary>
    PublicKey = 3,

    /// <summary>
    /// A private key that remains inside the platform key store. <see cref="CredentialRecord.Secret"/>
    /// contains only the opaque platform key id; the private key bytes do not exist in the vault.
    /// </summary>
    HardwareKey = 4,
}

/// <summary>
/// A secret the vault holds on the user's behalf.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="HostRecord"/> and sealed under its own key so that
/// drawing the host list never decrypts a password. One credential can serve
/// several hosts — a single deploy key across a fleet is the normal case — which
/// is why the link points from host to credential and not the other way.
/// </para>
/// <para>
/// For <see cref="CredentialKind.HardwareKey"/>, <see cref="Secret"/> is not key
/// material. It is the UTF-8 encoded opaque alias that identifies a non-exportable
/// key in the platform key store. <see cref="PublicKey"/> remains copyable so the
/// user can add it to a server's <c>authorized_keys</c> file.
/// </para>
/// <para>
/// The same sync bookkeeping as a host, for the same reason: these fields cannot
/// be added later to rows that are already encrypted on a device that may be
/// offline.
/// </para>
/// </remarks>
public sealed record CredentialRecord
{
    public required Guid Id { get; init; }

    /// <summary>What the user calls this credential, e.g. "deploy key".</summary>
    public required string Label { get; init; }

    public required CredentialKind Kind { get; init; }

    /// <summary>The user this credential authenticates as, when it is tied to one.</summary>
    public string? Username { get; init; }

    /// <summary>
    /// The password or private-key bytes for exportable credentials. For a
    /// <see cref="CredentialKind.HardwareKey"/>, this is only the UTF-8 encoded
    /// platform key id and never contains the private key.
    /// </summary>
    public required byte[] Secret { get; init; }

    /// <summary>The passphrase protecting <see cref="Secret"/>, when it is an encrypted private key.</summary>
    public byte[]? Passphrase { get; init; }

    /// <summary>
    /// The matching public key, kept so the key can be shown and copied into a
    /// server's authorized_keys without unsealing the private half.
    /// </summary>
    public string? PublicKey { get; init; }

    // Sync bookkeeping ----------------------------------------------------

    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? OriginDeviceId { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// The key's fingerprint or a fixed mask, whichever is safe to put on screen.
    /// </summary>
    public string DisplayHint => Kind switch
    {
        CredentialKind.Password => "••••••••",
        CredentialKind.PublicKey => PublicKey is null ? "public key" : Fingerprint(PublicKey),
        CredentialKind.HardwareKey => PublicKey is null ? "non-exportable key" : $"non-exportable · {Fingerprint(PublicKey)}",
        _ => PublicKey is null ? "private key" : Fingerprint(PublicKey),
    };

    private static string Fingerprint(string publicKey)
    {
        // An OpenSSH public key is "type base64 comment"; the comment is the part
        // a person recognises, and the base64 body is too long to show.
        var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3) return $"{parts[0]} · {parts[^1]}";
        return parts.Length > 0 ? parts[0] : "private key";
    }
}
