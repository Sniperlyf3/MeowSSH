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

        // AndroidX's fluent builder methods are annotated as nullable even
        // though they mutate and return this. Avoid chaining them so nullable
        // flow analysis does not interpret every step as a possible null
        // dereference when warnings are treated as errors.
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("MeowSSH session active");
        builder.SetContentText("Keeping your SSH connection alive in the background");
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(NotificationCompat.CategoryService);

        if (pendingIntent is not null)
            builder.SetContentIntent(pendingIntent);

        var notification = builder.Build()
            ?? throw new InvalidOperationException("Android could not create the foreground-service notification.");

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            ServiceCompat.StartForeground(this, NotificationId, notification, (int)ForegroundService.TypeSpecialUse);
        else
            ServiceCompat.StartForeground(this, NotificationId, notification, 0);

        // Restarting this service after Android kills the entire process would
        // be misleading: the SSH subprocess and socket are already gone by then.
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
