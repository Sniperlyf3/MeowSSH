using Android.Content;
using Android.Net;
using AndroidX.Core.Content;
using MeowSSH.Core.Services;
using MeowSSH.UI.Services;

namespace MeowSSH.App;

public sealed class AndroidTailcatVpnController : ITailcatVpnController, IDisposable
{
    private readonly object _gate = new();
    private TailcatVpnSnapshot _snapshot = new(false, null, []);

    public AndroidTailcatVpnController(ITailcatHubService hub)
    {
        TailcatVpnService.SetHub(hub);
        TailcatVpnService.StateChanged += OnServiceStateChanged;
    }

    public TailcatVpnSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler? Changed;

    public async Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Address)) throw new ArgumentException("A Tailcat address is required.", nameof(request));
        if (request.Routes.Count == 0 || request.Routes.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("At least one VPN route is required.", nameof(request));

        var context = global::Android.App.Application.Context;
        var prepare = VpnService.Prepare(context);
        if (prepare is not null)
        {
            var granted = await MainActivity.RequestVpnPermissionAsync(prepare, cancellationToken).ConfigureAwait(false);
            if (!granted) throw new InvalidOperationException("Android VPN permission was not granted.");
        }

        var intent = new Intent(context, typeof(TailcatVpnService));
        intent.SetAction(TailcatVpnService.ActionStart);
        intent.PutExtra(TailcatVpnService.ExtraAddress, request.Address.Trim());
        intent.PutExtra(TailcatVpnService.ExtraRoutes, string.Join('\n', request.Routes.Select(route => route.Trim())));
        intent.PutExtra(TailcatVpnService.ExtraClientKey, request.ClientKey?.Trim());
        intent.PutExtra(TailcatVpnService.ExtraDerpMapUrl, request.DerpMapUrl?.Trim());

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, TailcatVpnServiceStateChangedEventArgs e)
        {
            if (e.Running) tcs.TrySetResult(true);
            else if (!string.IsNullOrWhiteSpace(e.Error)) tcs.TrySetException(new InvalidOperationException(e.Error));
        }
        TailcatVpnService.StateChanged += Handler;
        try
        {
            ContextCompat.StartForegroundService(context, intent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { TailcatVpnService.StateChanged -= Handler; }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(TailcatVpnService));
        intent.SetAction(TailcatVpnService.ActionStop);
        context.StartService(intent);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Snapshot.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    private void OnServiceStateChanged(object? sender, TailcatVpnServiceStateChangedEventArgs state)
    {
        lock (_gate) _snapshot = new TailcatVpnSnapshot(state.Running, state.Address, state.Routes, state.Error);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => TailcatVpnService.StateChanged -= OnServiceStateChanged;
}
