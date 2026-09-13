using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace MeowSSH.App;

/// <summary>
/// Keeps the app process in Android's foreground-service state while the UI is
/// backgrounded so the Meowshell agent and its SSH socket continue to run.
/// </summary>
[Service(
    Name = "dev.sniperlyf3.meowssh.SessionKeepAliveService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class SessionKeepAliveService : Service
{
    private const string ChannelId = "active-ssh-session";
    private const int NotificationId = 1001;

    public override void OnCreate()
    {
        base.OnCreate();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(new NotificationChannel(
                ChannelId,
                "Active SSH session",
                NotificationImportance.Low)
            {
                Description = "Shown while MeowSSH keeps an SSH session alive in the background."
            });
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var pendingIntent = launchIntent is null
            ? null
            : PendingIntent.GetActivity(
                this,
                0,
                launchIntent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle("MeowSSH session active")
            .SetContentText("Keeping your SSH connection alive in the background")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetCategory(NotificationCompat.CategoryService)
            .SetContentIntent(pendingIntent)
            .Build();

        ServiceCompat.StartForeground(this, NotificationId, notification, (int)ForegroundService.TypeSpecialUse);

        // Restarting this service after Android kills the entire process would
        // be misleading: the SSH subprocess and socket are already gone by then.
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
