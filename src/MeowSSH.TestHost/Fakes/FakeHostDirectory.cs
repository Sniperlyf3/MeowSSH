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
public sealed class FakeHostDirectory : IHostDirectory
{
    private readonly List<HostStatus> _hosts;

    public FakeHostDirectory(bool empty = false)
    {
        _hosts = empty ? [] : BuildSample();
    }

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HostStatus>>(_hosts);

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
