using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// In-process SSH transport for browser tests. Each connection owns independent
/// shell and SFTP channels so multi-session UI behavior can be exercised without
/// a real SSH server.
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

        public Guid HostId { get; } = hostId;
        public bool IsConnected => !_disposed;
        public event EventHandler<SshConnectionLost>? ConnectionLost;

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

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            ConnectionLost = null;
            return ValueTask.CompletedTask;
        }
    }
}
