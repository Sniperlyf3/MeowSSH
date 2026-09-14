using MeowSSH.Core.Model;

namespace MeowSSH.Core.Ssh;

/// <summary>Used by hosts that cannot access Android USB hardware, including browser tests.</summary>
public sealed class UnsupportedSerialDeviceService : ISerialDeviceService
{
    public ValueTask<IReadOnlyList<SerialDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<SerialDeviceInfo>>([]);
    }

    public Task<ITerminalSession> OpenAsync(
        string deviceId,
        int baudRate,
        int dataBits,
        SerialStopBits stopBits,
        SerialParity parity,
        CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("USB serial terminals are available in the Android app.");
}
