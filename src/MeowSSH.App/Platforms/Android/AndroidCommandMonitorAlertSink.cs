using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.Content;
using MeowSSH.Core.Services;

namespace MeowSSH.App;

public sealed class AndroidCommandMonitorAlertSink : ICommandMonitorAlertSink
{
    private const string ChannelId = "meowssh-command-monitoring";

    public Task NotifyAsync(CommandMonitorAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        cancellationToken.ThrowIfCancellationRequested();

        var context = global::Android.App.Application.Context;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.PostNotifications)
                != Permission.Granted)
            return Task.CompletedTask;

        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null) return Task.CompletedTask;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Command monitoring",
                NotificationImportance.Default)
            {
                Description = "Alerts when a monitored SSH command fails, recovers, or changes output.",
            };
            manager.CreateNotificationChannel(channel);
        }

        Notification.Builder builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(context, ChannelId)
            : new Notification.Builder(context);

        var notification = builder
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle(alert.Title)
            .SetContentText(alert.Message)
            .SetStyle(new Notification.BigTextStyle().BigText(alert.Message))
            .SetAutoCancel(true)
            .SetCategory(Notification.CategoryStatus)
            .Build();

        manager.Notify(NotificationId(alert.MonitorId), notification);
        return Task.CompletedTask;
    }

    private static int NotificationId(Guid monitorId)
    {
        var value = monitorId.GetHashCode() & 0x7fffffff;
        return value == 0 ? 1 : value;
    }
}
