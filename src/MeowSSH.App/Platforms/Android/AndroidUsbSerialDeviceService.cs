using Android.Content;
using Android.Hardware.Usb;
using Anotherlab.UsbSerialForAndroid.Driver;
using Anotherlab.UsbSerialForAndroid.Extensions;
using Anotherlab.UsbSerialForAndroid.Util;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.App;

public sealed class AndroidUsbSerialDeviceService : ISerialDeviceService
{
    private static UsbManager Manager =>
        (UsbManager?)global::Android.App.Application.Context.GetSystemService(Context.UsbService)
        ?? throw new InvalidOperationException("Android USB host service is unavailable.");

    public async ValueTask<IReadOnlyList<SerialDeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drivers = await UsbSerialProber.GetDefaultProber().FindAllDriversAsync(Manager).ConfigureAwait(false);
        var result = new List<SerialDeviceInfo>();
        foreach (var driver in drivers)
        {
            for (var portIndex = 0; portIndex < driver.Ports.Count; portIndex++)
            {
                var device = driver.Device;
                var label = !string.IsNullOrWhiteSpace(device.ProductName)
                    ? device.ProductName
                    : driver.GetType().Name.Replace("SerialDriver", "", StringComparison.Ordinal);
                result.Add(new SerialDeviceInfo(
                    DeviceId(device.DeviceName, portIndex),
                    $"{label} · {device.VendorId:X4}:{device.ProductId:X4} · port {portIndex + 1}"));
            }
        }
        return result;
    }

    public async Task<ITerminalSession> OpenAsync(
        string deviceId,
        int baudRate,
        int dataBits,
        SerialStopBits stopBits,
        SerialParity parity,
        CancellationToken cancellationToken = default)
    {
        var (deviceName, portIndex) = ParseDeviceId(deviceId);
        var manager = Manager;
        var drivers = await UsbSerialProber.GetDefaultProber().FindAllDriversAsync(manager).ConfigureAwait(false);
        var driver = drivers.FirstOrDefault(d =>
            string.Equals(d.Device.DeviceName, deviceName, StringComparison.Ordinal));
        if (driver is null || portIndex < 0 || portIndex >= driver.Ports.Count)
            throw new IOException("The selected USB serial device is not connected.");

        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("MeowSSH cannot request USB permission while no Android activity is active.");
        if (!manager.HasPermission(driver.Device))
        {
            var granted = await manager.RequestPermissionAsync(driver.Device, activity).ConfigureAwait(false);
            if (!granted) throw new UnauthorizedAccessException("USB serial access was denied.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AndroidUsbSerialTerminal(
            manager,
            driver.Ports[portIndex],
            baudRate,
            dataBits,
            stopBits,
            parity);
    }

    private static string DeviceId(string deviceName, int portIndex) => $"{deviceName}#{portIndex}";

    private static (string DeviceName, int PortIndex) ParseDeviceId(string value)
    {
        var separator = value.LastIndexOf('#');
        if (separator <= 0 || !int.TryParse(value.AsSpan(separator + 1), out var portIndex))
            throw new ArgumentException("The saved USB serial device identifier is invalid.", nameof(value));
        return (value[..separator], portIndex);
    }

    private sealed class AndroidUsbSerialTerminal : ITerminalSession
    {
        private readonly UsbSerialPort _port;
        private readonly SerialInputOutputManager _io;
        private bool _disposed;

        public AndroidUsbSerialTerminal(
            UsbManager manager,
            UsbSerialPort port,
            int baudRate,
            int dataBits,
            SerialStopBits stopBits,
            SerialParity parity)
        {
            _port = port;
            _io = new SerialInputOutputManager(port)
            {
                BaudRate = baudRate,
                DataBits = dataBits,
                StopBits = stopBits switch
                {
                    SerialStopBits.One => StopBits.One,
                    SerialStopBits.OnePointFive => StopBits.OnePointFive,
                    SerialStopBits.Two => StopBits.Two,
                    _ => throw new ArgumentOutOfRangeException(nameof(stopBits)),
                },
                Parity = parity switch
                {
                    SerialParity.None => Parity.None,
                    SerialParity.Odd => Parity.Odd,
                    SerialParity.Even => Parity.Even,
                    SerialParity.Mark => Parity.Mark,
                    SerialParity.Space => Parity.Space,
                    _ => throw new ArgumentOutOfRangeException(nameof(parity)),
                },
            };
            _io.DataReceived += OnDataReceived;
            _io.ErrorReceived += OnErrorReceived;
            _io.Open(manager);
        }

        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;

        private void OnDataReceived(object? sender, SerialDataReceivedArgs args) =>
            OutputReceived?.Invoke(this, args.Data);

        private void OnErrorReceived(object? sender, UnhandledExceptionEventArgs args)
        {
            if (!_disposed) Exited?.Invoke(this, 1);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _port.Write(data.ToArray(), 1000);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _io.DataReceived -= OnDataReceived;
            _io.ErrorReceived -= OnErrorReceived;
            if (_io.IsOpen) _io.Close();
            _io.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
