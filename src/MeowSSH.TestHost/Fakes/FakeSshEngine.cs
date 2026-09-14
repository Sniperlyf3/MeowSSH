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
        return Task.FromResult<ISshConnection>(new FakeSshConnection(host.Id));
    }

    private sealed class FakeSshConnection(Guid hostId) : ISshConnection
    {
        private bool _disposed;
        private int _nextPort = 42000;

        public Guid HostId { get; } = hostId;
        public bool IsConnected => !_disposed;

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
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Local, listenAddress, remoteAddress, cancellationToken);

        public Task<ISshForward> OpenRemoteForwardAsync(
            string listenAddress,
            string localAddress,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Remote, listenAddress, localAddress, cancellationToken);

        public Task<ISshForward> OpenSocksForwardAsync(
            string listenAddress,
            bool requireAuth = true,
            CancellationToken cancellationToken = default) =>
            OpenForwardAsync(SshForwardKind.Socks, listenAddress, null, cancellationToken, requireAuth);

        private Task<ISshForward> OpenForwardAsync(
            SshForwardKind kind,
            string listenAddress,
            string? destination,
            CancellationToken cancellationToken,
            bool socksAuth = false)
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
                socksAuth ? "meowssh" : null,
                socksAuth ? "test-token" : null);
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
