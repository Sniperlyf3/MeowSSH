using Android.Content;
using AndroidX.Core.Content;
using MeowSSH.UI.Services;

namespace MeowSSH.App;

/// <summary>Keeps the app at foreground-service priority while long-lived network activities are active.</summary>
public sealed class AndroidActiveSessionLifetime : IActiveSessionLifetime
{
    private readonly object _gate = new();
    private int _users;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _users++;
            if (_users > 1) return Task.CompletedTask;
            var context = global::Android.App.Application.Context;
            ContextCompat.StartForegroundService(context, new Intent(context, typeof(SessionKeepAliveService)));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_users == 0) return Task.CompletedTask;
            _users--;
            if (_users > 0) return Task.CompletedTask;
            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(SessionKeepAliveService)));
        }
        return Task.CompletedTask;
    }
}
