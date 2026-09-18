using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>A USB serial endpoint the platform can currently see.</summary>
public sealed record SerialDeviceInfo(string Id, string Label);

/// <summary>Platform bridge for USB serial enumeration and opening.</summary>
public interface ISerialDeviceService
{
    ValueTask<IReadOnlyList<SerialDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);

    Task<ITerminalSession> OpenAsync(
        string deviceId,
        int baudRate,
        int dataBits,
        SerialStopBits stopBits,
        SerialParity parity,
        CancellationToken cancellationToken = default);
}

/// <summary>Routes saved serial endpoints through the platform USB serial bridge.</summary>
public sealed class SerialConnectionEngine(ISerialDeviceService devices) : IProtocolConnectionEngine
{
    public HostProtocol Protocol => HostProtocol.Serial;

    public Task<IHostConnection> ConnectAsync(
        HostRecord host,
        SshCredentials credentials,
        ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Address);
        IHostConnection connection = new SerialConnection(host.Id, host, devices);
        return Task.FromResult(connection);
    }

    private sealed class SerialConnection(Guid hostId, HostRecord host, ISerialDeviceService devices) : IHostConnection
    {
        private ITerminalSession? _terminal;
        private bool _disposed;

        public Guid HostId { get; } = hostId;
        public bool IsConnected => !_disposed;

        public SshPathStatus? PathStatus => null;
        public event EventHandler<SshPathStatus>? PathChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add { }
            remove { }
        }

        public async Task<ITerminalSession> OpenTerminalAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _terminal ??= await devices.OpenAsync(
                host.Address,
                host.SerialBaudRate,
                host.SerialDataBits,
                host.SerialStopBits,
                host.SerialParity,
                cancellationToken).ConfigureAwait(false);
            return _terminal;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_terminal is not null) await _terminal.DisposeAsync().ConfigureAwait(false);
        }
    }
}
