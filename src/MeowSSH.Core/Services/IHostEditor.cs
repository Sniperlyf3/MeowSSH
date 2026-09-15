using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

/// <summary>
/// What a credential looks like to the screens that list and choose between them.
/// </summary>
/// <remarks>
/// Deliberately has nowhere to put password/private-key material. A hardware-key
/// id is safe to expose because it is only an opaque platform alias, not a key.
/// </remarks>
public sealed record CredentialSummary(
    Guid Id,
    string Label,
    CredentialKind Kind,
    string? Username,
    string? PublicKey,
    string DisplayHint,
    string? HardwareKeyId = null);

public interface IHostEditor
{
    ValueTask SaveHostAsync(HostRecord host, CancellationToken cancellationToken = default);
    ValueTask DeleteHostAsync(Guid hostId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<CredentialSummary>> GetCredentialsAsync(CancellationToken cancellationToken = default);
    ValueTask SaveCredentialAsync(CredentialRecord credential, CancellationToken cancellationToken = default);
    ValueTask DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default);
}

public interface ICredentialResolver
{
    ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default);
    ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default);
}