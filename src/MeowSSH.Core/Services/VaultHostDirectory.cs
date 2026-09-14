using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

/// <summary>
/// The host list, the editor and the credential resolver, all reading the same
/// open vault.
/// </summary>
/// <remarks>
/// <para>
/// One class implementing three interfaces rather than three classes over one
/// store: they share the live connection states, which are not in the vault at
/// all. Connection state is deliberately not persisted — a host recorded as
/// "connected" in a file is a lie as soon as the app is killed, and showing a
/// stale green dot is worse than showing nothing.
/// </para>
/// <para>
/// Every read goes through <see cref="VaultStore.Document"/>, so locking the
/// vault makes the list throw rather than serve what it last saw. That is the
/// intent: a locked vault should have nothing to show.
/// </para>
/// </remarks>
public sealed class VaultHostDirectory : IHostDirectory, IHostEditor, ICredentialResolver, IDisposable
{
    private readonly VaultStore _store;
    private readonly Dictionary<Guid, (ConnectionState State, string? Detail)> _liveState = [];

    public VaultHostDirectory(VaultStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _store.Changed += OnStoreChanged;
    }

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default)
    {
        if (!_store.IsUnlocked) return ValueTask.FromResult<IReadOnlyList<HostStatus>>([]);

        IReadOnlyList<HostStatus> hosts =
        [
            .. _store.Document.LiveHosts.Select(host =>
                _liveState.TryGetValue(host.Id, out var live)
                    ? new HostStatus(host, live.State, live.Detail)
                    : new HostStatus(host))
        ];
        return ValueTask.FromResult(hosts);
    }

    /// <summary>Records what is happening to a connection right now. Not persisted.</summary>
    public void SetState(Guid hostId, ConnectionState state, string? detail = null)
    {
        if (state == ConnectionState.Disconnected && detail is null) _liveState.Remove(hostId);
        else _liveState[hostId] = (state, detail);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask SaveHostAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _store.UpdateAsync((document, now) => document.WithHost(host, now), cancellationToken);
    }

    public ValueTask DeleteHostAsync(Guid hostId, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync((document, now) => document.WithoutHost(hostId, now), cancellationToken);

    /// <summary>Stamps the host as reached just now, so the list orders by recency.</summary>
    public ValueTask RecordConnectionAsync(Guid hostId, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync((document, now) =>
        {
            var host = document.Hosts.FirstOrDefault(h => h.Id == hostId);
            return host is null ? document : document.WithHost(host with { LastConnectedAt = now }, now);
        }, cancellationToken);

    public ValueTask<IReadOnlyList<CredentialSummary>> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (!_store.IsUnlocked) return ValueTask.FromResult<IReadOnlyList<CredentialSummary>>([]);

        IReadOnlyList<CredentialSummary> summaries =
        [
            .. _store.Document.LiveCredentials.Select(c =>
                new CredentialSummary(c.Id, c.Label, c.Kind, c.Username, c.PublicKey, c.DisplayHint))
        ];
        return ValueTask.FromResult(summaries);
    }

    public ValueTask SaveCredentialAsync(CredentialRecord credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return _store.UpdateAsync((document, now) => document.WithCredential(credential, now), cancellationToken);
    }

    public ValueTask DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync((document, now) =>
        {
            // Any host still pointing at this credential is cleared in the same
            // save, so the file never holds a reference to a secret that is gone.
            var updated = document.WithoutCredential(credentialId, now);
            foreach (var host in document.Hosts.Where(h => h.CredentialId == credentialId && !h.IsDeleted))
                updated = updated.WithHost(host with { CredentialId = null }, now);
            return updated;
        }, cancellationToken);

    public ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var credential = FindCredential(host);
        if (credential is null)
            return ValueTask.FromResult(SshCredentials.None);

        // A fresh copy per attempt: SshCredentials is disposed by the caller once
        // the handshake is done, and handing out the vault's own arrays would mean
        // zeroing the record the vault is still holding.
        return ValueTask.FromResult(credential.Kind switch
        {
            CredentialKind.Password => new SshCredentials
            {
                Username = credential.Username,
                Password = SecretBuffer.CopyFrom(credential.Secret),
            },
            CredentialKind.PrivateKey => new SshCredentials
            {
                Username = credential.Username,
                PrivateKeys = [SecretBuffer.CopyFrom(credential.Secret)],
                KeyPassphrase = credential.Passphrase is null
                    ? null
                    : SecretBuffer.CopyFrom(credential.Passphrase),
            },
            _ => SshCredentials.None,
        });
    }

    public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var credential = FindCredential(host);
        return ValueTask.FromResult(credential?.Kind == CredentialKind.Password
            ? SecretBuffer.CopyFrom(credential.Secret)
            : null);
    }

    /// <summary>The passphrase for this host's private key, if it has one.</summary>
    public ValueTask<SecretBuffer?> ResolveKeyPassphraseAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var passphrase = FindCredential(host)?.Passphrase;
        return ValueTask.FromResult(passphrase is null ? null : SecretBuffer.CopyFrom(passphrase));
    }

    private CredentialRecord? FindCredential(HostRecord host)
    {
        if (!_store.IsUnlocked || host.CredentialId is not { } id) return null;
        var credential = _store.Document.Credentials.FirstOrDefault(c => c.Id == id);
        return credential?.IsDeleted == false ? credential : null;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _store.Changed -= OnStoreChanged;
}
