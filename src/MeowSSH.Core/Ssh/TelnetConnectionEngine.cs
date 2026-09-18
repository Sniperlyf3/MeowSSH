using System.Net.Sockets;
using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>Plain Telnet transport. Telnet is intentionally never used for credentials automatically.</summary>
public sealed class TelnetConnectionEngine : IProtocolConnectionEngine
{
    public HostProtocol Protocol => HostProtocol.Telnet;

    public async Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host.Address, host.Port, cancellationToken).ConfigureAwait(false);
            return new TelnetConnection(host.Id, client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            client.Dispose();
            if (ex is OperationCanceledException) throw;
            throw new SshException(SshFailure.NetworkUnreachable, "Could not reach the Telnet server.", ex);
        }
    }

    private sealed class TelnetConnection(Guid hostId, TcpClient client) : IHostConnection
    {
        private TelnetTerminalSession? _terminal;
        private bool _disposed;

        public Guid HostId { get; } = hostId;
        public bool IsConnected => !_disposed && client.Connected;
        public event EventHandler<SshConnectionLost>? ConnectionLost;

        public Task<ITerminalSession> OpenTerminalAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminal is not null) return Task.FromResult<ITerminalSession>(_terminal);

            _terminal = new TelnetTerminalSession(
                client.GetStream(),
                columns,
                rows,
                message => ConnectionLost?.Invoke(this, new SshConnectionLost(SshFailure.ConnectionLost, message)));
            return Task.FromResult<ITerminalSession>(_terminal);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_terminal is not null) await _terminal.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }

    private sealed class TelnetTerminalSession : ITerminalSession
    {
        private const byte Iac = 255;
        private const byte Dont = 254;
        private const byte Do = 253;
        private const byte Wont = 252;
        private const byte Will = 251;
        private const byte Sb = 250;
        private const byte Se = 240;
        private const byte Echo = 1;
        private const byte SuppressGoAhead = 3;
        private const byte TerminalType = 24;
        private const byte Naws = 31;
        private const byte TerminalTypeIs = 0;
        private const byte TerminalTypeSend = 1;

        private readonly NetworkStream _stream;
        private readonly Action<string> _onLost;
        private readonly CancellationTokenSource _stopped = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Task _pump;
        private int _columns;
        private int _rows;
        private bool _disposed;

        public TelnetTerminalSession(NetworkStream stream, int columns, int rows, Action<string> onLost)
        {
            _stream = stream;
            _columns = columns;
            _rows = rows;
            _onLost = onLost;
            _pump = PumpAsync();
        }

        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Telnet reserves 0xff for IAC. A literal 0xff is sent doubled.
            var extra = 0;
            foreach (var value in data.Span) if (value == Iac) extra++;
            if (extra == 0)
            {
                await WriteRawAsync(data, cancellationToken).ConfigureAwait(false);
                return;
            }

            var escaped = new byte[data.Length + extra];
            var j = 0;
            foreach (var value in data.Span)
            {
                escaped[j++] = value;
                if (value == Iac) escaped[j++] = Iac;
            }
            await WriteRawAsync(escaped, cancellationToken).ConfigureAwait(false);
        }

        public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            _columns = Math.Clamp(columns, 1, ushort.MaxValue);
            _rows = Math.Clamp(rows, 1, ushort.MaxValue);
            await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[16 * 1024];
            var output = new byte[16 * 1024];
            var state = ParserState.Data;
            byte command = 0;
            byte subOption = 0;
            var subData = new List<byte>(64);

            try
            {
                while (!_stopped.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(buffer, _stopped.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    var outCount = 0;

                    for (var i = 0; i < read; i++)
                    {
                        var value = buffer[i];
                        switch (state)
                        {
                            case ParserState.Data:
                                if (value == Iac) state = ParserState.Iac;
                                else output[outCount++] = value;
                                break;
                            case ParserState.Iac:
                                if (value == Iac)
                                {
                                    output[outCount++] = Iac;
                                    state = ParserState.Data;
                                }
                                else if (value is Do or Dont or Will or Wont)
                                {
                                    command = value;
                                    state = ParserState.Option;
                                }
                                else if (value == Sb)
                                {
                                    state = ParserState.SubOption;
                                }
                                else state = ParserState.Data;
                                break;
                            case ParserState.Option:
                                await NegotiateAsync(command, value, _stopped.Token).ConfigureAwait(false);
                                state = ParserState.Data;
                                break;
                            case ParserState.SubOption:
                                subOption = value;
                                subData.Clear();
                                state = ParserState.SubData;
                                break;
                            case ParserState.SubData:
                                if (value == Iac) state = ParserState.SubIac;
                                else subData.Add(value);
                                break;
                            case ParserState.SubIac:
                                if (value == Se)
                                {
                                    await HandleSubNegotiationAsync(subOption, subData, _stopped.Token).ConfigureAwait(false);
                                    state = ParserState.Data;
                                }
                                else
                                {
                                    if (value == Iac) subData.Add(Iac);
                                    state = ParserState.SubData;
                                }
                                break;
                        }
                    }

                    if (outCount > 0)
                        OutputReceived?.Invoke(this, output.AsMemory(0, outCount).ToArray());
                }

                Exited?.Invoke(this, 0);
            }
            catch (OperationCanceledException) when (_stopped.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                if (!_disposed) _onLost("The Telnet connection dropped.");
            }
        }

        private async Task NegotiateAsync(byte command, byte option, CancellationToken cancellationToken)
        {
            switch (command)
            {
                case Do when option == Naws:
                    await SendCommandAsync(Will, option, cancellationToken).ConfigureAwait(false);
                    await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case Do when option == TerminalType:
                    await SendCommandAsync(Will, option, cancellationToken).ConfigureAwait(false);
                    break;
                case Do:
                    await SendCommandAsync(Wont, option, cancellationToken).ConfigureAwait(false);
                    break;
                case Will when option is Echo or SuppressGoAhead:
                    await SendCommandAsync(Do, option, cancellationToken).ConfigureAwait(false);
                    break;
                case Will:
                    await SendCommandAsync(Dont, option, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task HandleSubNegotiationAsync(byte option, List<byte> data, CancellationToken cancellationToken)
        {
            if (option != TerminalType || data.Count == 0 || data[0] != TerminalTypeSend) return;
            var name = System.Text.Encoding.ASCII.GetBytes("xterm-256color");
            var frame = new byte[6 + name.Length];
            frame[0] = Iac;
            frame[1] = Sb;
            frame[2] = TerminalType;
            frame[3] = TerminalTypeIs;
            name.CopyTo(frame.AsSpan(4));
            frame[^2] = Iac;
            frame[^1] = Se;
            await WriteRawAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        private Task SendCommandAsync(byte command, byte option, CancellationToken cancellationToken) =>
            WriteRawAsync(new byte[] { Iac, command, option }, cancellationToken);

        private Task SendWindowSizeAsync(CancellationToken cancellationToken)
        {
            var cols = (ushort)_columns;
            var rows = (ushort)_rows;
            byte[] payload =
            [
                Iac, Sb, Naws,
                (byte)(cols >> 8), (byte)cols,
                (byte)(rows >> 8), (byte)rows,
                Iac, Se,
            ];
            return WriteRawAsync(payload, cancellationToken);
        }

        private async Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _stopped.CancelAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _stopped.Dispose();
            _writeGate.Dispose();
        }

        private enum ParserState
        {
            Data,
            Iac,
            Option,
            SubOption,
            SubData,
            SubIac,
        }
    }
}
