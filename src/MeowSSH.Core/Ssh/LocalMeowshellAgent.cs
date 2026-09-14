using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// Drives Meowshell's private local-agent subprocess. Unlike the normal
/// Meowshell agent, this transport never creates a socket and never invokes
/// Tailcat/DERP; stdin/stdout carry the framed protocol directly between the
/// app and a local PTY helper process.
/// </summary>
internal sealed class LocalMeowshellAgentConnection : IAsyncDisposable
{
    private const byte ControlFrame = 0;
    private const byte DataFrame = 1;
    private const int FrameHeaderLength = 5;
    private const int MaxFrameLength = 64 << 20;

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<uint, LocalMeowshellTerminalSession> _channels = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<uint>> _pendingOpens = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private long _nextRequestId;
    private bool _disposed;

    public event EventHandler<SshConnectionLost>? ConnectionLost;

    private LocalMeowshellAgentConnection(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static async Task<LocalMeowshellAgentConnection> StartAsync(
        MeowshellSshEngineOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var home = Path.Combine(options.WorkingDirectory, "local-terminal");
        EnsurePrivateDirectory(home);
        var binary = LocateMeowshell(options.BinaryDirectory);

        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("local-agent");
        psi.Environment["HOME"] = home;
        psi.Environment["TMPDIR"] = home;
        psi.Environment["MEOWSHELL_MANAGED_PARENT_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new IOException("Could not start the local Meowshell helper.");

        var connection = new LocalMeowshellAgentConnection(process);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardError.EndOfStream)
                    _ = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
            }
            catch { }
        });

        try
        {
            await connection.WriteControlAsync(0, new WireMessage
            {
                Msg = "configure",
                DisableAgent = true,
            }, cancellationToken).ConfigureAwait(false);

            var timeoutTask = Task.Delay(options.ConnectTimeout, cancellationToken);
            var settled = await Task.WhenAny(connection._connected.Task, timeoutTask).ConfigureAwait(false);
            if (settled != connection._connected.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The local terminal helper did not start in time.");
            }
            await connection._connected.Task.ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LocalMeowshellTerminalSession> OpenShellAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestId = "local-" + Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var opened = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingOpens.TryAdd(requestId, opened))
            throw new InvalidOperationException("Could not allocate a local terminal request.");

        try
        {
            await WriteControlAsync(0, new WireMessage
            {
                Msg = "open_channel",
                Kind = "shell",
                Pty = true,
                Cols = columns > 0 ? columns : 80,
                Rows = rows > 0 ? rows : 24,
                Term = "xterm-256color",
                RequestId = requestId,
            }, cancellationToken).ConfigureAwait(false);

            var id = await opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!_channels.TryGetValue(id, out var session))
                throw new IOException("The local terminal helper returned an unknown channel.");
            return session;
        }
        finally
        {
            _pendingOpens.TryRemove(requestId, out _);
        }
    }

    internal Task WriteDataAsync(uint channelId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        WriteFrameAsync(DataFrame, channelId, data, cancellationToken);

    internal Task ResizeAsync(uint channelId, int columns, int rows, CancellationToken cancellationToken) =>
        WriteControlAsync(channelId, new WireMessage
        {
            Msg = "resize",
            Cols = columns,
            Rows = rows,
        }, cancellationToken);

    internal async Task CloseChannelAsync(uint channelId)
    {
        if (_disposed) return;
        try
        {
            await WriteControlAsync(channelId, new WireMessage { Msg = "close_channel" }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception) when (_disposed || _process.HasExited) { }
        _channels.TryRemove(channelId, out _);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_stdout, _lifetime.Token).ConfigureAwait(false);
                if (frame.Type == ControlFrame)
                    HandleControl(frame.ChannelId, frame.Payload);
                else if (frame.Type == DataFrame)
                    HandleData(frame.ChannelId, frame.Payload);
                else
                    throw new IOException($"Unknown local-agent frame type {frame.Type}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            FailConnection(ex);
        }
    }

    private void HandleControl(uint channelId, byte[] payload)
    {
        var message = JsonSerializer.Deserialize<WireMessage>(payload)
            ?? throw new IOException("The local terminal helper sent an empty control message.");

        switch (message.Msg)
        {
            case "connected":
                _connected.TrySetResult();
                break;
            case "channel_opened":
            {
                if (message.RequestId is null || !_pendingOpens.TryGetValue(message.RequestId, out var pending))
                    return;
                var session = new LocalMeowshellTerminalSession(this, channelId);
                if (!_channels.TryAdd(channelId, session))
                {
                    pending.TrySetException(new IOException("The local terminal helper reused a channel ID."));
                    return;
                }
                pending.TrySetResult(channelId);
                break;
            }
            case "exit_status":
                if (_channels.TryRemove(channelId, out var exited))
                    exited.OnExited(message.ExitCode);
                break;
            case "error":
            {
                var error = new IOException(message.Message ?? "The local terminal helper reported an error.");
                if (message.RequestId is not null && _pendingOpens.TryGetValue(message.RequestId, out var pending))
                {
                    pending.TrySetException(error);
                    break;
                }
                if (channelId != 0 && _channels.TryRemove(channelId, out var failed))
                {
                    failed.OnFailure(error);
                    break;
                }
                FailConnection(error);
                break;
            }
        }
    }

    private void HandleData(uint channelId, byte[] payload)
    {
        if (payload.Length < 1 || !_channels.TryGetValue(channelId, out var session)) return;
        session.OnOutput(payload.AsMemory(1));
    }

    private void FailConnection(Exception ex)
    {
        _connected.TrySetException(ex);
        foreach (var pending in _pendingOpens.Values) pending.TrySetException(ex);
        foreach (var channel in _channels.Values) channel.OnFailure(ex);
        _channels.Clear();
        ConnectionLost?.Invoke(this, new SshConnectionLost(ex.Message));
    }

    private Task WriteControlAsync(uint channelId, WireMessage message, CancellationToken cancellationToken) =>
        WriteFrameAsync(ControlFrame, channelId, JsonSerializer.SerializeToUtf8Bytes(message), cancellationToken);

    private async Task WriteFrameAsync(byte type, uint channelId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameLength - FrameHeaderLength)
            throw new IOException("Local terminal protocol frame is too large.");

        var frameLength = FrameHeaderLength + payload.Length;
        var buffer = new byte[4 + frameLength];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)frameLength);
        buffer[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5, 4), channelId);
        payload.Span.CopyTo(buffer.AsSpan(9));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<WireFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length < FrameHeaderLength || length > MaxFrameLength)
            throw new IOException($"Invalid local terminal frame length {length}.");

        var body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new WireFrame(body[0], BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1, 4)), body[5..]);
    }

    private static string LocateMeowshell(string? binaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(binaryDirectory))
            throw new FileNotFoundException("The local Meowshell binary directory is not configured.");

        var fileName = OperatingSystem.IsAndroid()
            ? "libmeowshell.so"
            : OperatingSystem.IsWindows() ? "meowshell.exe" : "meowshell";
        var path = Path.Combine(binaryDirectory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("The local Meowshell helper binary is missing.", path);
        return path;
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!Directory.Exists(path)) Directory.CreateDirectory(path, ownerOnly);
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & shared) != 0) File.SetUnixFileMode(path, mode & ~shared);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { _stdin.Close(); } catch { }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        try { await _readLoop.ConfigureAwait(false); } catch { }
        _process.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private readonly record struct WireFrame(byte Type, uint ChannelId, byte[] Payload);

    private sealed class WireMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("msg")]
        public string? Msg { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string? Kind { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pty")]
        public bool? Pty { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("cols")]
        public int Cols { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("rows")]
        public int Rows { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("term")]
        public string? Term { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("exit_code")]
        public int ExitCode { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("disable_agent")]
        public bool DisableAgent { get; set; }
    }
}

internal sealed class LocalMeowshellTerminalSession(
    LocalMeowshellAgentConnection connection,
    uint channelId) : ITerminalSession
{
    private bool _disposed;

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return connection.WriteDataAsync(channelId, data, cancellationToken);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return connection.ResizeAsync(channelId, columns, rows, cancellationToken);
    }

    internal void OnOutput(ReadOnlyMemory<byte> data) => OutputReceived?.Invoke(this, data);
    internal void OnExited(int exitCode) => Exited?.Invoke(this, exitCode);
    internal void OnFailure(Exception _) => Exited?.Invoke(this, -1);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await connection.CloseChannelAsync(channelId).ConfigureAwait(false);
    }
}
