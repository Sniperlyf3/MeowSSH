using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

/// <summary>
/// What a credential looks like to the screens that list and choose between them.
/// </summary>
/// <remarks>
/// Deliberately has nowhere to put the secret. The rule that listing never
/// unseals a credential is worth more as a type the UI cannot misuse than as a
/// comment on a method that hands back the real record.
/// </remarks>
public sealed record CredentialSummary(
    Guid Id, string Label, CredentialKind Kind, string? Username, string? PublicKey, string DisplayHint);

/// <summary>
/// Adds, changes and removes what the vault holds.
/// </summary>
/// <remarks>
/// Separate from <see cref="IHostDirectory"/> so that a screen which only shows
/// hosts cannot change them, and so that the connection path — which needs
/// secrets — is a different dependency from the list path, which must not have
/// them.
/// </remarks>
public interface IHostEditor
{
    /// <summary>Adds a host, or replaces one with the same id.</summary>
    ValueTask SaveHostAsync(HostRecord host, CancellationToken cancellationToken = default);

    /// <summary>Removes a host, leaving a tombstone behind for sync.</summary>
    ValueTask DeleteHostAsync(Guid hostId, CancellationToken cancellationToken = default);

    /// <summary>Lists credentials without unsealing any of them.</summary>
    ValueTask<IReadOnlyList<CredentialSummary>> GetCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a password or private key.</summary>
    ValueTask SaveCredentialAsync(CredentialRecord credential, CancellationToken cancellationToken = default);

    /// <summary>Removes a credential and wipes its secret from the file now, not later.</summary>
    ValueTask DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces the key material for one connection attempt.
/// </summary>
/// <remarks>
/// The only route from the vault to the network. It exists as its own interface
/// so that the set of screens which can cause a secret to be decrypted stays
/// small enough to check by reading the constructor injections.
/// </remarks>
public interface ICredentialResolver
{
    /// <summary>
    /// Unseals whatever this host signs in with. The caller owns the result and
    /// must dispose it as soon as the handshake is done.
    /// </summary>
    ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stored password for this host, if it has one, for answering the
    /// server's prompt. Null when the host uses a key or nothing at all.
    /// </summary>
    ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default);
}
