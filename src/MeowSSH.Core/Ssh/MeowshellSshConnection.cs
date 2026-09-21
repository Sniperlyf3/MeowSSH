using System.Text;
using Meowshell;

namespace MeowSSH.Core.Ssh;

internal sealed class MeowshellSshConnection : ISshConnection, IConnectionPathTelemetry, IConnectionRelayHealth
{
    private readonly MeowshellAgentConnection _agent;

    public MeowshellSshConnection(Guid hostId, MeowshellAgentConnection agent)
    {
        HostId = hostId;
        _agent = agent;
        _agent.PathChanged += OnPathChanged;
        _agent.RelayHealthChanged += OnRelayHealthChanged;
    }

    public Guid HostId { get; }

    public bool IsConnected { get; private set; } = true;

    public SshPathStatus? PathStatus => TranslatePath(_agent.CurrentPath);

    public string? RelayHealth => _agent.CurrentRelayHealth;

    public event EventHandler<SshConnectionLost>? ConnectionLost;
    public event EventHandler<SshPathStatus>? PathChanged;
    public event EventHandler<string?>? RelayHealthChanged;

    public async Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _agent.OpenShellAsync(columns, rows, term: "xterm-256color", pty: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MeowshellSshShell(channel, OnChannelLost);
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
    }

    public async Task<SshCommandResult> RunCommandAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command must not be empty.", nameof(command));
        if (timeout is { } commandTimeout && commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");

        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        MeowshellAgentShellChannel? channel = null;
        try
        {
            channel = await _agent.OpenExecAsync([command], pty: false, cancellationToken: linkedCts.Token)
                .ConfigureAwait(false);

            var stdoutTask = ReadUtf8Async(channel.Output, linkedCts.Token);
            var stderrTask = ReadUtf8Async(channel.Error, linkedCts.Token);
            var exitCodeTask = channel.Completed.WaitAsync(linkedCts.Token);

            await Task.WhenAll(stdoutTask, stderrTask, exitCodeTask).ConfigureAwait(false);
            return new SshCommandResult(
                await exitCodeTask.ConfigureAwait(false),
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            if (channel is not null)
            {
                try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            throw new SshException(SshFailure.Timeout, $"The command exceeded the {timeout} timeout.", ex);
        }
        catch (OperationCanceledException)
        {
            if (channel is not null)
            {
                try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            throw;
        }
        catch (TailcatException ex)
        {
            throw MeowshellSshEngine.Translate(ex);
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ISftpSession>(new MeowshellSftpSession(_agent));

    public Task<ISshForward> OpenLocalForwardAsync(
        string listenAddress,
        string remoteAddress,
        bool allowNonLoopbackBind = false,
        int maxConnections = 256,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => _agent.OpenLocalForwardAsync(listenAddress, remoteAddress, allowNonLoopbackBind, maxConnections,
                cancellationToken),
            SshForwardKind.Local,
            remoteAddress);

    public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(
        string socketPath,
        string remoteAddress,
        int maxConnections = 256,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => _agent.OpenLocalForwardOnUnixSocketAsync(socketPath, remoteAddress, maxConnections, cancellationToken),
            SshForwardKind.Local,
            remoteAddress);

    public Task<ISshForward> OpenRemoteForwardAsync(
        string listenAddress,
        string localAddress,
        int maxConnections = 256,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => _agent.OpenRemoteForwardAsync(listenAddress, localAddress, maxConnections, cancellationToken),
            SshForwardKind.Remote,
            localAddress);

    public Task<ISshForward> OpenSocksForwardAsync(
        string listenAddress,
        bool requireAuth = true,
        string? socksUsername = null,
        string? socksPassword = null,
        bool allowNonLoopbackBind = false,
        int maxConnections = 256,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => _agent.OpenSocksForwardAsync(
                listenAddress,
                requireAuth,
                socksUsername,
                socksPassword,
                allowNonLoopbackBind,
                maxConnections,
                cancellationToken),
            SshForwardKind.Socks,
            null);

    public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(
        string socketPath,
        bool requireAuth = false,
        string? socksUsername = null,
        string? socksPassword = null,
        int maxConnections = 256,
        CancellationToken cancellationToken = default) =>
        OpenForwardAsync(
            () => _agent.OpenSocksForwardOnUnixSocketAsync(
                socketPath,
                requireAuth,
                socksUsername,
                socksPassword,
                maxConnections,
                cancellationToken),
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

    private static async Task<string> ReadUtf8Async(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static SshPathStatus? TranslatePath(MeowshellPathStatus? path) =>
        path is null ? null : new SshPathStatus(path.Direct, path.Via);

    private void OnPathChanged(MeowshellPathStatus path) =>
        PathChanged?.Invoke(this, new SshPathStatus(path.Direct, path.Via));

    private void OnRelayHealthChanged(string? problem) =>
        RelayHealthChanged?.Invoke(this, problem);

    private void OnChannelLost(SshConnectionLost lost)
    {
        IsConnected = false;
        ConnectionLost?.Invoke(this, lost);
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        _agent.PathChanged -= OnPathChanged;
        _agent.RelayHealthChanged -= OnRelayHealthChanged;
        await _agent.DisposeAsync().ConfigureAwait(false);
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