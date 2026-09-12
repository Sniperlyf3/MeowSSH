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

    // The agent multiplexes, so this is not a second connection and costs no
    // second authentication -- the whole reason one agent is kept per host.
    public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ISftpSession>(new MeowshellSftpSession(agent));

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

    /// <summary>
    /// Reads the channel's output and republishes it as events.
    /// </summary>
    /// <remarks>
    /// Whatever the read returns is forwarded immediately rather than being
    /// accumulated into lines. Terminal output is not line-oriented -- a shell
    /// prompt has no trailing newline, and buffering for one would leave the user
    /// looking at a blank screen waiting for a prompt that already arrived.
    /// </remarks>
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
            // Deliberate shutdown.
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
        try { await _channel.CloseAsync().ConfigureAwait(false); } catch (TailcatException) { /* already gone */ }
        try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _channel.DisposeAsync().ConfigureAwait(false);
        _pumpStopped.Dispose();
    }
}
