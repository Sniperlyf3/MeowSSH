using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Services;

/// <summary>
/// The host list, the editor and the credential resolver, all reading the same
/// open vault.
/// </summary>
public sealed class VaultHostDirectory : IHostDirectory, IHostEditor, ICredentialResolver, IDisposable
{
    private readonly VaultStore _store;
    private readonly ISshHardwareKeySigner? _hardwareKeySigner;
    private readonly Dictionary<Guid, (ConnectionState State, string? Detail)> _liveState = [];

    public VaultHostDirectory(VaultStore store, ISshHardwareKeySigner? hardwareKeySigner = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hardwareKeySigner = hardwareKeySigner;
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
                new CredentialSummary(
                    c.Id,
                    c.Label,
                    c.Kind,
                    c.Username,
                    c.PublicKey,
                    c.DisplayHint,
                    c.Kind == CredentialKind.HardwareKey ? Encoding.UTF8.GetString(c.Secret) : null))
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
            CredentialKind.HardwareKey => ResolveHardwareKey(credential),
            CredentialKind.PublicKey => SshCredentials.None,
            _ => SshCredentials.None,
        });
    }

    private SshCredentials ResolveHardwareKey(CredentialRecord credential)
    {
        if (_hardwareKeySigner is null)
            throw new InvalidOperationException("This device cannot use the selected non-exportable SSH key.");
        if (credential.Secret.Length == 0)
            throw new InvalidOperationException("The selected non-exportable SSH key has no platform key identifier.");
        if (string.IsNullOrWhiteSpace(credential.PublicKey))
            throw new InvalidOperationException("The selected non-exportable SSH key has no public key.");

        var keyId = Encoding.UTF8.GetString(credential.Secret);
        return new SshCredentials
        {
            Username = credential.Username,
            KeyStoreKeyIds = [keyId],
            KeyStorePublicKeys = [DecodeOpenSshPublicKey(credential.PublicKey)],
            HardwareKeySigner = _hardwareKeySigner,
        };
    }

    private static byte[] DecodeOpenSshPublicKey(string publicKey)
    {
        var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException("The selected non-exportable SSH key has an invalid public key.");
        try { return Convert.FromBase64String(parts[1]); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The selected non-exportable SSH key has an invalid public key.", ex);
        }
    }

    public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var credential = FindCredential(host);
        return ValueTask.FromResult(credential?.Kind == CredentialKind.Password
            ? SecretBuffer.CopyFrom(credential.Secret)
            : null);
    }

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
