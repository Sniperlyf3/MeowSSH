using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

/// <summary>
/// Adds opt-in session transcript capture below the page/session layer. The decorator preserves
/// the richer ISshConnection shape so SFTP, forwards and Actions do not lose capabilities.
/// </summary>
public sealed class SessionLoggingConnectionEngine(
    IConnectionEngine inner,
    ISessionLogService logs) : IConnectionEngine
{
    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        var connection = await inner.ConnectAsync(host, credentials, prompts, cancellationToken).ConfigureAwait(false);
        return connection is ISshConnection ssh
            ? new LoggingSshConnection(host, ssh, logs)
            : new LoggingHostConnection(host, connection, logs);
    }

    private class LoggingHostConnection(
        HostRecord host,
        IHostConnection innerConnection,
        ISessionLogService logs) : IHostConnection
    {
        protected IHostConnection Inner { get; } = innerConnection;

        public Guid HostId => Inner.HostId;
        public bool IsConnected => Inner.IsConnected;

        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add => Inner.ConnectionLost += value;
            remove => Inner.ConnectionLost -= value;
        }

        public async Task<ITerminalSession> OpenTerminalAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            var terminal = await Inner.OpenTerminalAsync(columns, rows, cancellationToken).ConfigureAwait(false);
            if (!logs.AutoRecord) return terminal;

            try
            {
                var capture = await logs.StartAsync(host, cancellationToken).ConfigureAwait(false);
                return new LoggingTerminalSession(terminal, capture);
            }
            catch (InvalidOperationException)
            {
                // An offline/cached Pro grant may expire while auto-record remains enabled.
                // Losing Pro must never prevent the user from opening a terminal.
                return terminal;
            }
        }

        public virtual ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class LoggingSshConnection(
        HostRecord host,
        ISshConnection ssh,
        ISessionLogService logs)
        : LoggingHostConnection(host, ssh, logs), ISshConnection
    {
        public Task<SshCommandResult> RunCommandAsync(
            string command,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            ssh.RunCommandAsync(command, timeout, cancellationToken);

        public Task<ISshShell> OpenShellAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default) =>
            OpenLoggedShellAsync(columns, rows, cancellationToken);

        private async Task<ISshShell> OpenLoggedShellAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken)
        {
            var shell = await ssh.OpenShellAsync(columns, rows, cancellationToken).ConfigureAwait(false);
            if (!logs.AutoRecord) return shell;

            try
            {
                var capture = await logs.StartAsync(host, cancellationToken).ConfigureAwait(false);
                return new LoggingSshShell(shell, capture);
            }
            catch (InvalidOperationException)
            {
                return shell;
            }
        }

        public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) =>
            ssh.OpenSftpAsync(cancellationToken);

        public Task<ISshForward> OpenLocalForwardAsync(
            string listenAddress,
            string remoteAddress,
            bool allowNonLoopbackBind = false,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            ssh.OpenLocalForwardAsync(listenAddress, remoteAddress, allowNonLoopbackBind, maxConnections, cancellationToken);

        public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(
            string socketPath,
            string remoteAddress,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            ssh.OpenLocalForwardOnUnixSocketAsync(socketPath, remoteAddress, maxConnections, cancellationToken);

        public Task<ISshForward> OpenRemoteForwardAsync(
            string listenAddress,
            string localAddress,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            ssh.OpenRemoteForwardAsync(listenAddress, localAddress, maxConnections, cancellationToken);

        public Task<ISshForward> OpenSocksForwardAsync(
            string listenAddress,
            bool requireAuth = true,
            string? socksUsername = null,
            string? socksPassword = null,
            bool allowNonLoopbackBind = false,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            ssh.OpenSocksForwardAsync(
                listenAddress,
                requireAuth,
                socksUsername,
                socksPassword,
                allowNonLoopbackBind,
                maxConnections,
                cancellationToken);

        public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(
            string socketPath,
            bool requireAuth = false,
            string? socksUsername = null,
            string? socksPassword = null,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            ssh.OpenSocksForwardOnUnixSocketAsync(
                socketPath,
                requireAuth,
                socksUsername,
                socksPassword,
                maxConnections,
                cancellationToken);
    }

    private class LoggingTerminalSession : ITerminalSession
    {
        private readonly ITerminalSession _terminal;
        private readonly ISessionLogCapture _capture;
        private bool _disposed;

        public LoggingTerminalSession(ITerminalSession terminal, ISessionLogCapture capture)
        {
            _terminal = terminal;
            _capture = capture;
            _terminal.OutputReceived += OnOutputReceived;
            _terminal.Exited += OnExited;
        }

        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _terminal.WriteAsync(data, cancellationToken);

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
            _terminal.ResizeAsync(columns, rows, cancellationToken);

        private void OnOutputReceived(object? sender, ReadOnlyMemory<byte> output)
        {
            _capture.TryAppend(output);
            OutputReceived?.Invoke(this, output);
        }

        private void OnExited(object? sender, int exitCode)
        {
            _ = _capture.CompleteAsync(
                exitCode,
                exitCode == 0 ? "Terminal exited." : $"Terminal exited with status {exitCode}.",
                CancellationToken.None);
            Exited?.Invoke(this, exitCode);
        }

        public virtual async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _terminal.OutputReceived -= OnOutputReceived;
            _terminal.Exited -= OnExited;
            await _capture.CompleteAsync(endReason: "Session closed.", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await _terminal.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class LoggingSshShell(ISshShell shell, ISessionLogCapture capture)
        : LoggingTerminalSession(shell, capture), ISshShell;
}
