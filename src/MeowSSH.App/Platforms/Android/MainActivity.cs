using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace MeowSSH.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    private const int VpnPermissionRequestCode = 4817;
    private static readonly object VpnPermissionGate = new();
    private static TaskCompletionSource<bool>? _vpnPermission;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content is not null) ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsets());
    }

    public static Task<bool> RequestVpnPermissionAsync(Intent intent, CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException("The MeowSSH activity is not available to request VPN permission.");
        TaskCompletionSource<bool> tcs;
        lock (VpnPermissionGate)
        {
            if (_vpnPermission is { Task.IsCompleted: false })
                throw new InvalidOperationException("A VPN permission request is already in progress.");
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _vpnPermission = tcs;
        }

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        activity.RunOnUiThread(() => activity.StartActivityForResult(intent, VpnPermissionRequestCode));
        return tcs.Task;
    }

#pragma warning disable CS0672
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
#pragma warning restore CS0672
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnPermissionRequestCode) return;
        TaskCompletionSource<bool>? pending;
        lock (VpnPermissionGate)
        {
            pending = _vpnPermission;
            _vpnPermission = null;
        }
        pending?.TrySetResult(resultCode == Result.Ok);
    }

    private sealed class SystemBarInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null) return insets ?? WindowInsetsCompat.Consumed!;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            var keyboard = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            if (bars is not null) view.SetPadding(bars.Left, bars.Top, bars.Right, keyboard?.Bottom ?? 0);
            return insets;
        }
    }
}
