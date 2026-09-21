using MeowSSH.UI.Services;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Counts Start/Stop calls and mirrors <c>AndroidActiveSessionLifetime</c>'s
/// own ref-counting (an extra Start while already active is a no-op past the
/// first; an extra Stop past zero is a no-op too) so a test exercising a
/// caller's Start/Stop pairing sees the same tolerance the real platform
/// implementation has, while still being able to assert exactly how many
/// times each was actually called.
/// </summary>
public sealed class FakeActiveSessionLifetime : IActiveSessionLifetime
{
    private int _users;

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool IsActive => _users > 0;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCalls++;
        _users++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCalls++;
        if (_users > 0) _users--;
        return Task.CompletedTask;
    }
}
