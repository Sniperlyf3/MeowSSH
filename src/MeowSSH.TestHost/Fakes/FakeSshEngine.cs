using Microsoft.AspNetCore.Components;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// In-process SSH transport for browser tests. Each connection owns independent
/// shell, SFTP and forwarding channels so multi-session UI behavior can be exercised
/// without a real SSH server.
/// </summary>
/// <remarks>
/// A Tailcat connection opened with "relayhealth" in the query string (e.g.
/// <c>NewPageAsync("/?multi&amp;relayhealth")</c>) starts with a non-null
/// <see cref="IConnectionRelayHealth.RelayHealth"/>, same piggyback on
/// query-string parsing as <see cref="FakeEntitlementService"/>'s "free". The
/// literal text is the sample from the admission-refusal contract, not a code
/// this fake looks up -- the UI is expected to render it verbatim.
/// </remarks>
public sealed class FakeSshEngine(NavigationManager navigation) : ISshEngine
{
    internal const string OverQuotaRelayHealth = "MeowSSH managed relay: monthly usage allowance exceeded";

    public Task<ISshConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isTailcat = host.Transport == SshTransport.Tailcat;
        var query = new Uri(navigation.Uri).Query;
        var relayHealth = isTailcat && query.Contains("relayhealth", StringComparison.OrdinalIgnoreCase)
            ? OverQuotaRelayHealth
            : null;
        return Task.FromResult<ISshConnection>(new FakeSshConnection(host.Id, isTailcat, relayHealth));
    }

    private sealed class FakeSshConnection : ISshConnection, IConnectionPathTelemetry, IConnectionRelayHealth
    {
        private bool _disposed;
        private int _nextPort = 42000;
        private EventHandler<string?>? _relayHealthChanged;

        public FakeSshConnection(Guid hostId, bool isTailcat, string? relayHealth)
        {
            HostId = hostId;
            PathStatus = isTailcat ? new SshPathStatus(false, "ci") : null;
            RelayHealth = relayHealth;
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

        public string? RelayHealth { get; private set; }

        // Deliberately does NOT replay the current value on subscribe (unlike
        // PathChanged above): a caller must read RelayHealth itself before
        // subscribing, exactly as MeowshellAgentConnection's real contract
        // works, or a session that already knows its relay is unhealthy at
        // load/reconnect time would never show anything until the next
        // (possibly nonexistent) change.
        public event EventHandler<string?>? RelayHealthChanged
        {
            add => _relayHealthChanged += value;
            remove => _relayHealthChanged -= value;
        }

        /// <summary>Test-only hook: <see cref="FakeSshShell"/> calls this for a typed "relayhealth ..." command.</summary>
        private void SetRelayHealth(string? problem)
        {
            if (problem == RelayHealth) return;
            RelayHealth = problem;
            _relayHealthChanged?.Invoke(this, problem);
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
            ISshShell shell = new FakeSshShell(SetRelayHealth);
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
