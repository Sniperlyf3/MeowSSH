using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// Opens a real local PTY through Meowshell's private local-agent transport.
/// The helper is connected only through redirected process stdin/stdout: no
/// Tailcat address, DERP relay, DNS lookup, TCP/UDP socket, or Internet access
/// is involved in creating or using the terminal transport.
/// </summary>
public sealed class LocalTerminalConnectionEngine(MeowshellSshEngineOptions options) : IProtocolConnectionEngine
{
    public HostProtocol Protocol => HostProtocol.Local;

    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        LocalMeowshellAgentConnection? agent = null;
        try
        {
            agent = await LocalMeowshellAgentConnection.StartAsync(options, cancellationToken).ConfigureAwait(false);
            return new LocalTerminalConnection(host.Id, agent);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or TimeoutException)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            throw new SshException(SshFailure.Unknown, $"Could not start the local terminal: {ex.Message}", ex);
        }
    }

    private sealed class LocalTerminalConnection(
        Guid hostId,
        LocalMeowshellAgentConnection agent) : IHostConnection
    {
        private bool _disposed;

        public Guid HostId { get; } = hostId;
        public bool IsConnected => !_disposed;
        public event EventHandler<SshConnectionLost>? ConnectionLost;

        public async Task<ITerminalSession> OpenTerminalAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await agent.OpenShellAsync(columns, rows, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            agent.ConnectionLost -= OnAgentConnectionLost;
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        private void OnAgentConnectionLost(object? sender, SshConnectionLost e) => ConnectionLost?.Invoke(this, e);

        public LocalTerminalConnection
        {
            agent.ConnectionLost += OnAgentConnectionLost;
        }
    }
}
