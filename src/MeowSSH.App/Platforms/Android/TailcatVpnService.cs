using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using MeowSSH.Core.Services;
using System.Globalization;

namespace MeowSSH.App;

public sealed class TailcatVpnServiceStateChangedEventArgs : EventArgs
{
    public TailcatVpnServiceStateChangedEventArgs(bool running, string? address, IReadOnlyList<string> routes, string? error = null)
    {
        Running = running;
        Address = address;
        Routes = routes;
        Error = error;
    }

    public bool Running { get; }
    public string? Address { get; }
    public IReadOnlyList<string> Routes { get; }
    public string? Error { get; }
}

[Service(Exported = false, Permission = "android.permission.BIND_VPN_SERVICE", ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(["android.net.VpnService"])]
public sealed class TailcatVpnService : VpnService
{
    public const string ActionStart = "dev.sniperlyf3.meowssh.tailcatvpn.START";
    public const string ActionStop = "dev.sniperlyf3.meowssh.tailcatvpn.STOP";
    public const string ExtraAddress = "tailcat.address";
    public const string ExtraRoutes = "tailcat.routes";
    public const string ExtraClientKey = "tailcat.clientKey";
    public const string ExtraDerpMapUrl = "tailcat.derpMapUrl";

    private const string NotificationChannelId = "tailcat-vpn";
    private const int NotificationId = 4818;
    private static readonly object StaticGate = new();
    private static ITailcatHubService? _hub;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ParcelFileDescriptor? _tun;
    private Task<int>? _nativeTask;
    private bool _ownsSocks;
    private bool _stopping;
    private string? _address;
    private IReadOnlyList<string> _routes = [];

    public static event EventHandler<TailcatVpnServiceStateChangedEventArgs>? StateChanged;

    internal static void SetHub(ITailcatHubService hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        lock (StaticGate) _hub = hub;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop) _ = StopTunnelAsync(stopService: true);
        else if (intent?.Action == ActionStart) _ = StartTunnelAsync(intent);
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        _ = StopTunnelAsync(stopService: true);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        _ = StopTunnelAsync(stopService: false);
        _lifecycle.Dispose();
        base.OnDestroy();
    }

    private async Task StartTunnelAsync(Intent intent)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_tun is not null) return;
            _stopping = false;
            _address = intent.GetStringExtra(ExtraAddress)?.Trim();
            _routes = ParseRoutes(intent.GetStringExtra(ExtraRoutes));
            var clientKey = NullIfWhiteSpace(intent.GetStringExtra(ExtraClientKey));
            var derpMapUrl = NullIfWhiteSpace(intent.GetStringExtra(ExtraDerpMapUrl));
            if (string.IsNullOrWhiteSpace(_address)) throw new InvalidOperationException("A Tailcat address is required for VPN mode.");
            if (_routes.Count == 0) throw new InvalidOperationException("At least one VPN route is required.");

            EnsureNotificationChannel();
            StartForeground(NotificationId, BuildNotification("Starting Tailcat VPN…"));

            ITailcatHubService hub;
            lock (StaticGate) hub = _hub ?? throw new InvalidOperationException("Tailcat VPN services are not initialized.");

            TailcatSocksSnapshot socks;
            if (hub.Snapshot.Socks is { } existing)
            {
                socks = existing;
                _ownsSocks = false;
            }
            else
            {
                await hub.StartSocksAsync(new TailcatSocksRequest("127.0.0.1:0", clientKey, derpMapUrl)).ConfigureAwait(false);
                socks = hub.Snapshot.Socks ?? throw new InvalidOperationException("Tailcat SOCKS gateway did not start.");
                _ownsSocks = true;
            }

            var (socksHost, socksPort) = ParseListenAddress(socks.ListenAddress);
            if (socksHost is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("VPN mode requires a loopback Tailcat SOCKS gateway.");

            var builder = new Builder(this)
                .SetSession("MeowSSH Tailcat")
                .SetMtu(8500)
                .AddAddress("198.18.0.1", 32);

            var packageName = PackageName ?? throw new InvalidOperationException("Android did not provide the MeowSSH package name.");
            builder.AddDisallowedApplication(packageName);
            foreach (var route in _routes)
            {
                var (network, prefix) = ParseIpv4Cidr(route);
                builder.AddRoute(network, prefix);
            }
            if (_routes.Any(route => route == "0.0.0.0/0")) builder.AddDnsServer("1.1.1.1");

            _tun = builder.Establish() ?? throw new InvalidOperationException("Android could not establish the VPN interface.");
            var config = BuildHevConfig(socksHost == "localhost" ? "127.0.0.1" : socksHost, socksPort);
            var fd = _tun.Fd;
            _nativeTask = Task.Run(() => HevSocks5TunnelNative.Run(config, fd));
            _ = ObserveNativeAsync(_nativeTask);

            StartForeground(NotificationId, BuildNotification($"Tailcat VPN · {_routes.Count} route{(_routes.Count == 1 ? "" : "s")}"));
            Publish(new TailcatVpnServiceStateChangedEventArgs(true, _address, _routes));
        }
        catch (Exception ex)
        {
            await CleanupAsync().ConfigureAwait(false);
            Publish(new TailcatVpnServiceStateChangedEventArgs(false, _address, _routes, ex.Message));
            StopSelf();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ObserveNativeAsync(Task<int> task)
    {
        try
        {
            var exitCode = await task.ConfigureAwait(false);
            if (!_stopping)
            {
                Publish(new TailcatVpnServiceStateChangedEventArgs(false, _address, _routes, $"The TUN-to-SOCKS bridge stopped unexpectedly (code {exitCode})."));
                await StopTunnelAsync(stopService: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                Publish(new TailcatVpnServiceStateChangedEventArgs(false, _address, _routes, $"The TUN-to-SOCKS bridge failed: {ex.Message}"));
                await StopTunnelAsync(stopService: true).ConfigureAwait(false);
            }
        }
    }

    private async Task StopTunnelAsync(bool stopService)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            if (_nativeTask is not null)
            {
                try { HevSocks5TunnelNative.Quit(); } catch { }
                try { await _nativeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
            await CleanupAsync().ConfigureAwait(false);
            Publish(new TailcatVpnServiceStateChangedEventArgs(false, null, []));
            StopForeground(StopForegroundFlags.Remove);
            if (stopService) StopSelf();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task CleanupAsync()
    {
        try { _tun?.Close(); } catch { }
        _tun?.Dispose();
        _tun = null;
        _nativeTask = null;

        if (_ownsSocks)
        {
            ITailcatHubService? hub;
            lock (StaticGate) hub = _hub;
            if (hub is not null)
            {
                try { await hub.StopSocksAsync().ConfigureAwait(false); } catch { }
            }
        }
        _ownsSocks = false;
    }

    private static string BuildHevConfig(string host, int port) => $"""
        tunnel:
          mtu: 8500
          ipv4: 198.18.0.1
        socks5:
          address: '{host}'
          port: {port.ToString(CultureInfo.InvariantCulture)}
          udp: 'udp'
        misc:
          log-file: null
          log-level: warn
          connect-timeout: 15000
          tcp-read-write-timeout: 300000
          udp-read-write-timeout: 60000
        """;

    private static string[] ParseRoutes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private static (string Network, int Prefix) ParseIpv4Cidr(string value)
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            throw new InvalidOperationException($"VPN route '{value}' is not a valid IPv4 CIDR.");
        return (ip.ToString(), prefix);
    }

    private static (string Host, int Port) ParseListenAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Tailcat returned an empty SOCKS listen address.");
        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var end = trimmed.LastIndexOf(']');
            if (end <= 0 || end + 2 > trimmed.Length || !int.TryParse(trimmed[(end + 2)..], out var port6)) throw new InvalidOperationException($"Invalid SOCKS listen address: {value}");
            return (trimmed[1..end], port6);
        }
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(trimmed[(colon + 1)..], out var port)) throw new InvalidOperationException($"Invalid SOCKS listen address: {value}");
        return (trimmed[..colon], port);
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(NotificationChannelId, "Tailcat VPN", NotificationImportance.Low)
        {
            Description = "Shows when MeowSSH is routing device traffic through Tailcat."
        });
    }

    private Notification BuildNotification(string text)
    {
        var packageName = PackageName ?? throw new InvalidOperationException("Android did not provide the MeowSSH package name.");
        var manager = PackageManager ?? throw new InvalidOperationException("Android package manager is unavailable.");
        var launch = manager.GetLaunchIntentForPackage(packageName);
        var pending = launch is null ? null : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, NotificationChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("MeowSSH Tailcat VPN");
        builder.SetContentText(text);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        if (pending is not null) builder.SetContentIntent(pending);
        return builder.Build() ?? throw new InvalidOperationException("Android could not create the Tailcat VPN notification.");
    }

    private static void Publish(TailcatVpnServiceStateChangedEventArgs state) => StateChanged?.Invoke(null, state);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
