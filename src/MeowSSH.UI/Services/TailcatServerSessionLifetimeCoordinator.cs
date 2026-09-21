using MeowSSH.Core.Services;

namespace MeowSSH.UI.Services;

/// <summary>
/// Owns the app's <see cref="IActiveSessionLifetime"/> hold for the Tailcat
/// server, independent of which panel started it and independent of whether
/// that panel is still on screen.
/// </summary>
/// <remarks>
/// <c>TailcatPhonePanel</c> ("Share this phone") and <c>TailcatTemporarySharePanel</c>
/// used to acquire/release the wakelock themselves, only from their own
/// Start/Stop/Revoke click handlers. That missed every *spontaneous* end: a
/// temporary share's own deadline, or the general panel's server hitting the
/// same deadline (both drive <c>MeowshellServer.StartDeadline</c> -&gt;
/// SIGTERM/SIGKILL), or the server being stopped from whichever panel isn't
/// the one currently mounted. None of those call a Stop/Revoke button, so
/// nothing ever called <see cref="IActiveSessionLifetime.StopAsync"/> -- the
/// app kept a wakelock alive for sharing that had already ended, a battery
/// drain with no visible cause since neither panel needs to be open for it to
/// happen.
///
/// The fix is to derive the hold entirely from
/// <see cref="ITailcatHubService"/>.Snapshot.Server instead of any panel's own
/// click, the same signal <c>TailcatTemporaryShareService.Active</c> already
/// re-derives its own "is this share still really alive" state from and for
/// the same reason (see its remarks). This type is constructed once --
/// <c>AppRoot</c> injects it purely to force that early construction -- and
/// its subscription to <see cref="ITailcatHubService.Changed"/> outlives every
/// panel, so a deadline that fires while neither panel is on screen still
/// releases the wakelock.
///
/// Re-reading the snapshot fresh on every <see cref="ITailcatHubService.Changed"/>
/// event, rather than trusting anything captured when a Start/Stop was kicked
/// off, is also what makes this safe against two known races:
/// <list type="bullet">
/// <item>the hub raises <c>Changed</c> twice for one explicit stop --
/// <c>StopServerAsync</c>'s own raise, and <c>ObserveServerAsync</c>'s
/// trailing raise once disposal actually completes. <see cref="_held"/> only
/// flips once, so the second event is a no-op.</item>
/// <item>a share started immediately after another one ended -- a late,
/// duplicate event from the *old* server's teardown lands after the *new*
/// server is already up, but by then <see cref="ITailcatHubService.Snapshot"/>
/// reports the new server, so re-reading it (instead of asking "is the server
/// that raised this event still the current one") never mistakes that stray
/// event for the new share ending.</item>
/// </list>
/// </remarks>
public sealed class TailcatServerSessionLifetimeCoordinator : IDisposable
{
    private readonly ITailcatHubService _hub;
    private readonly IActiveSessionLifetime _lifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _held;
    private bool _disposed;

    public TailcatServerSessionLifetimeCoordinator(ITailcatHubService hub, IActiveSessionLifetime lifetime)
    {
        _hub = hub;
        _lifetime = lifetime;
        _hub.Changed += OnHubChanged;
        // Covers a server already running at construction time. Cannot happen
        // today -- nothing can start one before this is constructed -- but
        // costs nothing to make true regardless of construction order.
        _ = SyncAsync();
    }

    private void OnHubChanged(object? sender, EventArgs e) => _ = SyncAsync();

    private async Task SyncAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                var running = _hub.Snapshot.Server is not null;
                if (running && !_held)
                {
                    // Flip only after the platform call succeeds, so a failed
                    // StartAsync leaves _held false and the very next Changed
                    // event (there is always another one -- a retry, or the
                    // eventual stop) gets another chance to converge.
                    await _lifetime.StartAsync().ConfigureAwait(false);
                    _held = true;
                }
                else if (!running && _held)
                {
                    await _lifetime.StopAsync().ConfigureAwait(false);
                    _held = false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Best-effort: a failed platform-lifetime call must not fault this
            // fire-and-forget handler. Swallowing here is safe precisely
            // because _held was not flipped on failure -- nothing is lost.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hub.Changed -= OnHubChanged;
    }
}
