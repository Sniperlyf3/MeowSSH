using MeowSSH.Core.Model;
using Meowshell;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// Runs Meowshell's shell server inside this app and connects back to its
/// ephemeral tailcat address. This gives Android a real PTY without requiring a
/// system shell executable to be driven directly from MAUI.
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
        var localRoot = Path.Combine(options.WorkingDirectory, "local-terminal");
        var serverHome = Path.Combine(localRoot, "server");
        var clientHome = Path.Combine(localRoot, "client");
        Directory.CreateDirectory(serverHome);
        Directory.CreateDirectory(clientHome);

        MeowshellServer? server = null;
        MeowshellAgentConnection? agent = null;
        try
        {
            var serverOptions = MeowshellOptions.Create(TimeSpan.FromHours(12)) with
            {
                HomeDirectory = serverHome,
                WorkDirectory = serverHome,
                BinaryDirectory = options.BinaryDirectory,
                InsecureNoAuth = true,
                EphemeralKey = true,
                FullAddress = true,
            };
            server = await MeowshellServer.StartAsync(serverOptions, cancellationToken).ConfigureAwait(false);

            var clientOptions = new TailcatClientOptions
            {
                HomeDirectory = clientHome,
                BinaryDirectory = options.BinaryDirectory!,
                Timeout = options.ConnectTimeout,
                Verbose = options.Verbose,
            };
            agent = await MeowshellAgentConnection.ConnectAsync(
                clientOptions,
                server.Address,
                configure: new MeowshellAgentConfigureOptions { DisableLocalAgent = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new LocalTerminalConnection(host.Id, server, agent);
        }
        catch (Exception ex) when (ex is TailcatException or IOException or FileNotFoundException or TimeoutException)
        {
            if (agent is not null) await agent.DisposeAsync().ConfigureAwait(false);
            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
            if (ex is TailcatException tailcat) throw MeowshellSshEngine.Translate(tailcat);
            throw new SshException(SshFailure.Unknown, $"Could not start the local terminal: {ex.Message}", ex);
        }
    }

    private sealed class LocalTerminalConnection(
        Guid hostId,
        MeowshellServer server,
        MeowshellAgentConnection agent) : IHostConnection
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
            try
            {
                var channel = await agent.OpenShellAsync(
                    columns,
                    rows,
                    term: "xterm-256color",
                    pty: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return new MeowshellSshShell(channel, lost => ConnectionLost?.Invoke(this, lost));
            }
            catch (TailcatException ex)
            {
                throw MeowshellSshEngine.Translate(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await agent.DisposeAsync().ConfigureAwait(false); }
            finally { await server.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
