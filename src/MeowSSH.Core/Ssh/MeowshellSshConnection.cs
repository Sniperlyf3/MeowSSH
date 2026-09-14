using Meowshell;

namespace MeowSSH.Core.Ssh;

internal sealed class MeowshellSshConnection(Guid hostId, MeowshellAgentConnection agent) : ISshConnection
{
    public Guid HostId { get; } = hostId;

    public bool IsConnected { get; private set; } = true;

    public event EventHandler<SshConnectionLost>? ConnectionLost;

    public async Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await agent.OpenShellAsync(columns, rows, term: "xterm-256color", pty: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MeowshellSshShell(channel, OnChannelLost);
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
    }

    public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ISftpSession>(new MeowshellSftpSession(agent));

    public Task<ISshForward> OpenLocalForwardAsync(
        string listenAddress,
        string remoteAddress,
        bool allowNonLoopbackBind = false,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => agent.OpenLocalForwardAsync(listenAddress, remoteAddress, allowNonLoopbackBind,
                cancellationToken: cancellationToken),
            SshForwardKind.Local,
            remoteAddress);

    public Task<ISshForward> OpenRemoteForwardAsync(
        string listenAddress,
        string localAddress,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => agent.OpenRemoteForwardAsync(listenAddress, localAddress, cancellationToken: cancellationToken),
            SshForwardKind.Remote,
            localAddress);

    public Task<ISshForward> OpenSocksForwardAsync(
        string listenAddress,
        bool requireAuth = true,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => agent.OpenSocksForwardAsync(listenAddress, requireAuth,
                cancellationToken: cancellationToken),
            SshForwardKind.Socks,
            null);

    private static async Task<ISshForward> OpenForwardAsync(
        Func<Task<MeowshellForward>> open,
        SshForwardKind kind,
        string? destination)
    {
        try
        {
            var forward = await open().ConfigureAwait(false);
            return new MeowshellSshForward(forward, kind, destination);
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
    }

    private void OnChannelLost(SshConnectionLost lost)
    {
        IsConnected = false;
        ConnectionLost?.Invoke(this, lost);
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        await agent.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class MeowshellSshForward(MeowshellForward forward, SshForwardKind kind, string? destination) : ISshForward
{
    public SshForwardKind Kind { get; } = kind;
    public string BoundAddress => forward.BoundAddress;
    public string? Destination { get; } = destination;
    public string? SocksUsername => forward.SocksUsername;
    public string? SocksPassword => forward.SocksPassword;

    public Task CloseAsync(CancellationToken cancellationToken = default) => forward.CloseAsync(cancellationToken);

    public ValueTask DisposeAsync() => forward.DisposeAsync();
}

internal sealed class MeowshellSshShell : ISshShell
{
    private readonly MeowshellAgentShellChannel _channel;
    private readonly CancellationTokenSource _pumpStopped = new();
    private readonly Task _pump;

    public MeowshellSshShell(MeowshellAgentShellChannel channel, Action<SshConnectionLost> onLost)
    {
        _channel = channel;
        _pump = PumpAsync(onLost);
    }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    private async Task PumpAsync(Action<SshConnectionLost> onLost)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_pumpStopped.IsCancellationRequested)
            {
                var read = await _channel.Output.ReadAsync(buffer, _pumpStopped.Token).ConfigureAwait(false);
                if (read <= 0) break;
                OutputReceived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
            }

            var exitCode = await _channel.Completed.ConfigureAwait(false);
            Exited?.Invoke(this, exitCode);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            onLost(new SshConnectionLost(
                ex is TailcatException tex ? MeowshellSshEngine.Translate(tex).Failure : SshFailure.ConnectionLost,
                "The connection to this host dropped."));
        }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _channel.WriteAsync(data, cancellationToken);

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
        _channel.ResizeAsync(columns, rows, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _pumpStopped.CancelAsync().ConfigureAwait(false);
        try { await _channel.CloseAsync().ConfigureAwait(false); } catch (TailcatException) { }
        try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _channel.DisposeAsync().ConfigureAwait(false);
        _pumpStopped.Dispose();
    }
}
