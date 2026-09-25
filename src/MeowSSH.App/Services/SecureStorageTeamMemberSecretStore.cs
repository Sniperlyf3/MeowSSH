using MeowSSH.Core.Services;

namespace MeowSSH.App.Services;

/// <summary>
/// Android Keystore-backed storage for this phone's team identity: a random
/// secret of its own, separate from the heartbeat and cloud-backup ones.
/// Losing it (an uninstall) makes this phone a stranger to its team; the
/// owner removes the old entry and sends a new invite.
/// </summary>
public sealed class SecureStorageTeamMemberSecretStore : ITeamMemberSecretStore
{
    private const string Key = "meowssh.team.member.v1";

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var secret = await SecureStorage.Default.GetAsync(Key).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    public Task SaveAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        cancellationToken.ThrowIfCancellationRequested();
        return SecureStorage.Default.SetAsync(Key, secret);
    }
}
