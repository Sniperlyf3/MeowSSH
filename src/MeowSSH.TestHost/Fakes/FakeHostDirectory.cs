using MeowSSH.Core.Model;
using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// A host list with no SSH engine behind it, so the UI can be driven on Linux CI
/// without an emulator or a real server.
/// </summary>
/// <remarks>
/// The sample covers the cases that actually change how a row renders: each
/// transport, each connection state, a long label that must ellipsize, and a
/// tailcat address long enough to need truncating. A fake that only ever returns
/// three tidy rows tests nothing.
/// </remarks>
public sealed class FakeHostDirectory : IHostDirectory, IHostEditor
{
    private readonly List<HostStatus> _hosts;

    public FakeHostDirectory(bool empty = false)
    {
        _hosts = empty ? [] : BuildSample();
    }

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HostStatus>>(_hosts);

    // Editing is in memory here on purpose. These tests drive the UI; what the
    // vault does with a saved host is proven against the real store in
    // MeowSSH.Core.Tests, where a browser would add nothing.
    private readonly List<CredentialRecord> _credentials = [];

    public ValueTask SaveHostAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        var index = _hosts.FindIndex(h => h.Host.Id == host.Id);
        if (index < 0) _hosts.Add(new HostStatus(host));
        else _hosts[index] = _hosts[index] with { Host = host };
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteHostAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        _hosts.RemoveAll(h => h.Host.Id == hostId);
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CredentialSummary>> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<CredentialSummary>>(
        [
            .. _credentials.Select(c =>
                new CredentialSummary(c.Id, c.Label, c.Kind, c.Username, c.PublicKey, c.DisplayHint))
        ]);

    public ValueTask SaveCredentialAsync(CredentialRecord credential, CancellationToken cancellationToken = default)
    {
        _credentials.Add(credential);
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        _credentials.RemoveAll(c => c.Id == credentialId);
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    /// <summary>Moves a host to a new state, as a real connection attempt would.</summary>
    public void SetState(string label, ConnectionState state, string? detail = null)
    {
        var index = _hosts.FindIndex(h => h.Host.Label == label);
        if (index < 0) return;
        _hosts[index] = _hosts[index] with { State = state, Detail = detail };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<HostStatus> BuildSample()
    {
        var now = DateTimeOffset.UtcNow;
        HostRecord Host(string label, string address, SshTransport transport, string? user, int port = 22) => new()
        {
            Id = Guid.NewGuid(),
            Label = label,
            Address = address,
            Username = user,
            Port = port,
            Transport = transport,
            UpdatedAt = now,
        };

        return
        [
            new(Host("prod-web-01", "10.4.2.11", SshTransport.Tcp, "deploy"), ConnectionState.Connected, "18 ms"),
            new(Host("build-runner", "build.internal", SshTransport.Tcp, "ci", 2222), ConnectionState.Connecting),
            new(Host("home-nas", "tcAbCdEf0123456789GhIjKlMnOpQrStUvWxYz", SshTransport.Tailcat, null)),
            new(Host("pi-garage", "100.84.12.7", SshTransport.TailscaleSsh, "pi")),
            new(Host("staging-db-replica-eu-west", "db-replica.staging.example.com", SshTransport.Tcp, "postgres"),
                ConnectionState.Error, "Host key changed"),
            new(Host("bastion", "bastion.example.com", SshTransport.Tcp, "jump")),
        ];
    }
}
