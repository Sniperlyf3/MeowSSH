using MeowSSH.Core.Services;

namespace MeowSSH.App.Services;

/// <summary>
/// Android Keystore-backed storage for this phone's heartbeat-monitor
/// identity: a random secret, not derived from the vault or the Google account.
/// Losing it (an uninstall) orphans the monitors on the server, which is why
/// the monitors page shows their ping URLs are only on the phone that made them.
/// </summary>
public sealed class SecureStorageMonitorOwnerSecretStore : IMonitorOwnerSecretStore
{
    private const string Key = "meowssh.heartbeat.owner.v1";

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
