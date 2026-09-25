using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using AndroidX.Core.Content;
using AndroidX.Work;
using MeowSSH.Core.Services;

namespace MeowSSH.App;

/// <summary>
/// The background half of heartbeat monitoring: WorkManager wakes this every
/// ~15 minutes (Android's floor for periodic work), even with the app closed
/// or after a reboot, and it asks MeowSSHAPI for the monitors' states.
/// </summary>
/// <remarks>
/// Polling rather than FCM push on purpose: no Firebase project to configure,
/// and no device token for the server to hold. The price is up to ~15 minutes
/// of extra latency, well inside the grace windows these monitors use.
/// </remarks>
[Register("dev.sniperlyf3.meowssh.HeartbeatCheckWorker")]
public sealed class HeartbeatCheckWorker : Worker
{
    public HeartbeatCheckWorker(Context context, WorkerParameters parameters) : base(context, parameters)
    {
    }

    public override Result DoWork()
    {
        var service = IPlatformApplication.Current?.Services.GetService<IHeartbeatMonitorService>();
        if (service is null) return Result.InvokeRetry()!;
        try
        {
            service.CheckForAlertsAsync().GetAwaiter().GetResult();
            return Result.InvokeSuccess()!;
        }
        catch (Exception exception) when (exception is HeartbeatMonitorException or HttpRequestException or TaskCanceledException)
        {
            // Offline or the service is busy: try again on WorkManager's
            // backoff rather than waiting a whole period to notice an outage.
            return Result.InvokeRetry()!;
        }
    }
}

public sealed class AndroidHeartbeatCheckScheduler : IHeartbeatCheckScheduler
{
    private const string WorkName = "meowssh-heartbeat-check";

    public void Update(bool anyMonitors)
    {
        var manager = WorkManager.GetInstance(global::Android.App.Application.Context);
        if (!anyMonitors)
        {
            manager.CancelUniqueWork(WorkName);
            return;
        }

        var request = new PeriodicWorkRequest.Builder(typeof(HeartbeatCheckWorker), TimeSpan.FromMinutes(15))
            .SetConstraints(new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build())
            .Build();
        // Keep, not replace: re-enqueuing on every list would restart the
        // 15-minute clock and could postpone a check indefinitely.
        manager.EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Keep!, (PeriodicWorkRequest)request);
    }
}

public sealed class AndroidHeartbeatAlertSink : IHeartbeatAlertSink
{
    private const string ChannelId = "meowssh-heartbeat-monitors";

    public Task NotifyAsync(HeartbeatAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var context = global::Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            return Task.CompletedTask;

        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return Task.CompletedTask;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Heartbeat monitors", NotificationImportance.High)
        {
            Description = "Alerts when a monitored job stops checking in, reports a failure, or recovers.",
        });

        var notification = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle(alert.Title)
            .SetContentText(alert.Message)
            .SetAutoCancel(true)
            .SetCategory(Notification.CategoryAlarm)
            .Build();
        // One notification per monitor: a recovery replaces its outage alert
        // instead of stacking beside it.
        manager.Notify(("heartbeat:" + alert.MonitorId).GetHashCode(StringComparison.Ordinal) & 0x7fffffff, notification);
        return Task.CompletedTask;
    }
}

public sealed class AndroidNotificationConsent : INotificationConsent
{
    public async Task<bool> EnsureAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;
        var status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>).ConfigureAwait(false);
        return status == PermissionStatus.Granted;
    }
}
