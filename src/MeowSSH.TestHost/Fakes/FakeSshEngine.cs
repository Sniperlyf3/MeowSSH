using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// In-process SSH transport for browser tests. Each connection owns independent
/// shell, SFTP and forwarding channels so multi-session UI behavior can be exercised
/// without a real SSH server.
/// </summary>
public sealed class FakeSshEngine : ISshEngine
{
    public Task<ISshConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISshConnection>(new FakeSshConnection(host.Id, host.Transport == SshTransport.Tailcat));
    }

    private sealed class FakeSshConnection : ISshConnection
    {
        private bool _disposed;
        private int _nextPort = 42000;

        public FakeSshConnection(Guid hostId, bool isTailcat)
        {
            HostId = hostId;
            PathStatus = isTailcat ? new SshPathStatus(false, "ci") : null;
        }

        public Guid HostId { get; }
        public bool IsConnected => !_disposed;
        public SshPathStatus? PathStatus { get; }

        public event EventHandler<SshPathStatus>? PathChanged
        {
            add
            {
                if (value is not null && PathStatus is { } path)
                    value(this, path);
            }
            remove { }
        }

        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add { }
            remove { }
        }

        public Task<ISshShell> OpenShellAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            ISshShell shell = new FakeSshShell();
            return Task.FromResult(shell);
        }

        public Task<SshCommandResult> RunCommandAsync(
            string command,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command must not be empty.", nameof(command));
            if (timeout is { } commandTimeout && commandTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            if (command.Contains("meowssh_uptime=", StringComparison.Ordinal))
            {
                return Task.FromResult(new SshCommandResult(
                    0,
                    "meowssh_os=Linux\n" +
                    "meowssh_uptime=up 2 hours\n" +
                    "meowssh_load=0.10 0.20 0.30\n" +
                    "meowssh_disk=42%\n" +
                    "meowssh_memory=37%\n",
                    string.Empty));
            }

            var failed = command.Contains("fail", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(failed
                ? new SshCommandResult(1, string.Empty, $"fake failure on {HostId}")
                : new SshCommandResult(0, $"{command} on {HostId}", string.Empty));
        }

        public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            ISftpSession sftp = new FakeSftpSession();
            return Task.FromResult(sftp);
        }

        public Task<ISshForward> OpenLocalForwardAsync(
            string listenAddress,
            string remoteAddress,
            bool allowNonLoopbackBind = false,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Local, listenAddress, remoteAddress, cancellationToken);

        public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(
            string socketPath,
            string remoteAddress,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Local, socketPath, remoteAddress, cancellationToken);

        public Task<ISshForward> OpenRemoteForwardAsync(
            string listenAddress,
            string localAddress,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Remote, listenAddress, localAddress, cancellationToken);

        public Task<ISshForward> OpenSocksForwardAsync(
            string listenAddress,
            bool requireAuth = true,
            string? socksUsername = null,
            string? socksPassword = null,
            bool allowNonLoopbackBind = false,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(
                SshForwardKind.Socks,
                listenAddress,
                null,
                cancellationToken,
                requireAuth,
                socksUsername,
                socksPassword);

        public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(
            string socketPath,
            bool requireAuth = false,
            string? socksUsername = null,
            string? socksPassword = null,
            int maxConnections = 256,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(
                SshForwardKind.Socks,
                socketPath,
                null,
                cancellationToken,
                requireAuth,
                socksUsername,
                socksPassword);

        private Task<ISshForward> OpenForwardAsync(
            SshForwardKind kind,
            string listenAddress,
            string? destination,
            CancellationToken cancellationToken,
            bool socksAuth = false,
            string? socksUsername = null,
            string? socksPassword = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bound = listenAddress.EndsWith(":0", StringComparison.Ordinal)
                ? $"127.0.0.1:{_nextPort++}"
                : listenAddress;
            ISshForward forward = new FakeSshForward(
                kind,
                bound,
                destination,
                socksAuth ? socksUsername ?? "meowssh" : null,
                socksAuth ? socksPassword ?? "test-token" : null);
            return Task.FromResult(forward);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSshForward(
        SshForwardKind kind,
        string boundAddress,
        string? destination,
        string? socksUsername,
        string? socksPassword) : ISshForward
    {
        public SshForwardKind Kind { get; } = kind;
        public string BoundAddress { get; } = boundAddress;
        public string? Destination { get; } = destination;
        public string? SocksUsername { get; } = socksUsername;
        public string? SocksPassword { get; } = socksPassword;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
