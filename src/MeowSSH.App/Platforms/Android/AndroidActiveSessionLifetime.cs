using Android.Content;
using AndroidX.Core.Content;
using MeowSSH.UI.Services;

namespace MeowSSH.App;

/// <summary>
/// Keeps the application process in foreground-service priority for exactly the
/// lifetime of a user-initiated SSH session.
/// </summary>
public sealed class AndroidActiveSessionLifetime : IActiveSessionLifetime
{
    private readonly object _gate = new();
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_started) return Task.CompletedTask;

            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(SessionKeepAliveService));
            ContextCompat.StartForegroundService(context, intent);
            _started = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_started) return Task.CompletedTask;

            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(SessionKeepAliveService)));
            _started = false;
        }

        return Task.CompletedTask;
    }
}
