using System.Text;
using Microsoft.AspNetCore.Components;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeHostDirectory : IHostDirectory, IHostEditor
{
    private readonly List<HostStatus> _hosts;
    private readonly List<CredentialRecord> _credentials = [];

    /// <summary>
    /// Private, and must stay private: DI resolves this type by constructor,
    /// and a second public constructor it could also pick makes activation
    /// ambiguous and fails the whole host at startup.
    /// </summary>
    private FakeHostDirectory(bool empty) => _hosts = empty ? [] : BuildSample();

    /// <summary>
    /// The constructor DI uses. Starts with an empty vault when the page was
    /// opened with "nohosts" in the query string (e.g.
    /// <c>NewPageAsync("/?nohosts")</c>), the same string-Contains flag
    /// mechanism <see cref="FakeEntitlementService"/> and Playground.razor
    /// already use.
    /// </summary>
    /// <remarks>
    /// Until this existed there was no way for a UI test to reach any "you
    /// have no hosts yet" state -- which is exactly the state C7's Actions and
    /// Host Health empty states exist to render, so they would have shipped
    /// untestable. This directory is scoped per circuit, so a test opting in
    /// gets its own empty vault without touching the sample one every other
    /// test is written against; no existing query string contains "nohosts"
    /// ("newhost", the nearest, does not).
    /// </remarks>
    public FakeHostDirectory(NavigationManager navigation)
        : this(new Uri(navigation.Uri).Query.Contains("nohosts", StringComparison.OrdinalIgnoreCase))
    {
    }

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HostStatus>>(_hosts);

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
            .. _credentials.Select(c => new CredentialSummary(
                c.Id,
                c.Label,
                c.Kind,
                c.Username,
                c.PublicKey,
                c.DisplayHint,
                c.Kind == CredentialKind.HardwareKey ? Encoding.UTF8.GetString(c.Secret) : null))
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