using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

/// <summary>
/// Decorates the protocol router with credential-isolated ProxyJump support for
/// saved SSH hosts. Intermediate hosts are authenticated with their own vault
/// credentials and the next hop is reached through an authenticated loopback
/// SOCKS5 forward, so destination credentials are never offered to a bastion.
/// </summary>
public sealed class ProxyJumpConnector(
    IHostDirectory hosts,
    ICredentialResolver credentialResolver,
    ConnectionEngine inner,
    ISshPrompts basePrompts) : IConnectionEngine
{
    private const int MaxJumpDepth = 8;

    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(prompts);

        var chain = await ResolveChainAsync(host, cancellationToken).ConfigureAwait(false);
        if (chain.Count == 1)
            return await inner.ConnectAsync(host, credentials, prompts, cancellationToken).ConfigureAwait(false);

        var openedConnections = new List<IHostConnection>();
        var openedForwards = new List<ISshForward>();
        string? proxyUrl = null;

        try
        {
            for (var index = 0; index < chain.Count; index++)
            {
                var source = chain[index];
                var isFinal = index == chain.Count - 1;

                if (source.Protocol != HostProtocol.Ssh)
                    throw new SshException(SshFailure.Unknown, $"Jump host ‘{source.Label}’ is not an SSH connection.");

                if (index > 0 && !string.IsNullOrWhiteSpace(source.ProxyUrl))
                    throw new SshException(
                        SshFailure.Unknown,
                        $"‘{source.Label}’ has its own upstream proxy configured. A host inside a jump chain cannot also use a separate upstream proxy.");

                var routed = source with
                {
                    JumpHostId = null,
                    ProxyUrl = index == 0 ? source.ProxyUrl : proxyUrl,
                };

                IHostConnection connection;
                if (isFinal)
                {
                    connection = await inner.ConnectAsync(
                        routed,
                        credentials,
                        prompts,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var hopCredentials = await credentialResolver.ResolveAsync(source, cancellationToken).ConfigureAwait(false);
                    var hop = !string.IsNullOrWhiteSpace(hopCredentials.Username)
                        ? routed with { Username = hopCredentials.Username }
                        : routed;
                    var hopPrompts = new CredentialSshPrompts(basePrompts, hopCredentials);
                    connection = await inner.ConnectAsync(
                        hop,
                        hopCredentials,
                        hopPrompts,
                        cancellationToken).ConfigureAwait(false);
                }

                openedConnections.Add(connection);

                if (isFinal)
                {
                    var wrapped = new ChainedSshConnection(
                        host.Id,
                        connection,
                        openedConnections,
                        openedForwards);
                    openedConnections = [];
                    openedForwards = [];
                    return wrapped;
                }

                if (connection is not ISshConnection ssh)
                    throw new SshException(
                        SshFailure.Unknown,
                        $"Jump host ‘{source.Label}’ does not provide SSH port forwarding.");

                var socks = await ssh.OpenSocksForwardAsync(
                    "127.0.0.1:0",
                    requireAuth: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                openedForwards.Add(socks);
                proxyUrl = BuildSocksProxyUrl(socks);
            }

            throw new InvalidOperationException("Jump chain did not contain a final destination.");
        }
        catch
        {
            await DisposePartialAsync(openedConnections, openedForwards).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<HostRecord>> ResolveChainAsync(
        HostRecord destination,
        CancellationToken cancellationToken)
    {
        if (destination.JumpHostId is null) return [destination];
        if (destination.Protocol != HostProtocol.Ssh)
            throw new SshException(SshFailure.Unknown, "Only SSH connections can use a jump host.");

        var statuses = await hosts.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        var byId = statuses
            .Where(status => !status.Host.IsDeleted)
            .ToDictionary(status => status.Host.Id, status => status.Host);
        var reversed = new List<HostRecord> { destination };
        var seen = new HashSet<Guid> { destination.Id };
        var current = destination;
        var intermediateCount = 0;

        while (current.JumpHostId is { } jumpId)
        {
            intermediateCount++;
            if (intermediateCount > MaxJumpDepth)
                throw new SshException(SshFailure.Unknown, $"Jump-host chains are limited to {MaxJumpDepth} intermediate hosts.");
            if (!seen.Add(jumpId))
                throw new SshException(SshFailure.Unknown, "The configured jump-host chain contains a cycle.");
            if (!byId.TryGetValue(jumpId, out var jump))
                throw new SshException(SshFailure.NotFound, "A configured jump host no longer exists.");
            if (jump.Protocol != HostProtocol.Ssh)
                throw new SshException(SshFailure.Unknown, $"Jump host ‘{jump.Label}’ is not an SSH connection.");

            reversed.Add(jump);
            current = jump;
        }

        reversed.Reverse();
        return reversed;
    }

    private static string BuildSocksProxyUrl(ISshForward forward)
    {
        if (string.IsNullOrWhiteSpace(forward.SocksUsername)
            || string.IsNullOrWhiteSpace(forward.SocksPassword))
            throw new InvalidOperationException("The jump-host SOCKS forward did not return authentication credentials.");

        return $"socks5://{Uri.EscapeDataString(forward.SocksUsername)}:{Uri.EscapeDataString(forward.SocksPassword)}@{forward.BoundAddress}";
    }

    private static async Task DisposePartialAsync(
        List<IHostConnection> connections,
        List<ISshForward> forwards)
    {
        for (var i = forwards.Count - 1; i >= 0; i--)
        {
            try { await forwards[i].DisposeAsync().ConfigureAwait(false); } catch { }
        }
        for (var i = connections.Count - 1; i >= 0; i--)
        {
            try { await connections[i].DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private sealed class ChainedSshConnection : ISshConnection
    {
        private readonly ISshConnection _final;
        private readonly List<IHostConnection> _connections;
        private readonly List<ISshForward> _forwards;
        private bool _disposed;

        public ChainedSshConnection(
            Guid hostId,
            IHostConnection final,
            List<IHostConnection> connections,
            List<ISshForward> forwards)
        {
            HostId = hostId;
            _final = final as ISshConnection
                ?? throw new InvalidOperationException("The final jump-chain connection is not SSH.");
            _connections = [.. connections];
            _forwards = [.. forwards];
            foreach (var connection in _connections)
                connection.ConnectionLost += OnConnectionLost;
        }

        public Guid HostId { get; }
        public bool IsConnected => !_disposed && _connections.All(connection => connection.IsConnected);
        public event EventHandler<SshConnectionLost>? ConnectionLost;

        public Task<SshCommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            _final.RunCommandAsync(command, timeout, cancellationToken);

        public Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
            _final.OpenShellAsync(columns, rows, cancellationToken);

        public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) =>
            _final.OpenSftpAsync(cancellationToken);

        public Task<ISshForward> OpenLocalForwardAsync(string listenAddress, string remoteAddress, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) =>
            _final.OpenLocalForwardAsync(listenAddress, remoteAddress, allowNonLoopbackBind, maxConnections, cancellationToken);

        public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(string socketPath, string remoteAddress, int maxConnections = 256, CancellationToken cancellationToken = default) =>
            _final.OpenLocalForwardOnUnixSocketAsync(socketPath, remoteAddress, maxConnections, cancellationToken);

        public Task<ISshForward> OpenRemoteForwardAsync(string listenAddress, string localAddress, int maxConnections = 256, CancellationToken cancellationToken = default) =>
            _final.OpenRemoteForwardAsync(listenAddress, localAddress, maxConnections, cancellationToken);

        public Task<ISshForward> OpenSocksForwardAsync(string listenAddress, bool requireAuth = true, string? socksUsername = null, string? socksPassword = null, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) =>
            _final.OpenSocksForwardAsync(listenAddress, requireAuth, socksUsername, socksPassword, allowNonLoopbackBind, maxConnections, cancellationToken);

        public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(string socketPath, bool requireAuth = false, string? socksUsername = null, string? socksPassword = null, int maxConnections = 256, CancellationToken cancellationToken = default) =>
            _final.OpenSocksForwardOnUnixSocketAsync(socketPath, requireAuth, socksUsername, socksPassword, maxConnections, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var connection in _connections)
                connection.ConnectionLost -= OnConnectionLost;
            await DisposePartialAsync(_connections, _forwards).ConfigureAwait(false);
        }

        private void OnConnectionLost(object? sender, SshConnectionLost e) => ConnectionLost?.Invoke(this, e);
    }
}
