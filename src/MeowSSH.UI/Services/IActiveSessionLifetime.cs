namespace MeowSSH.UI.Services;

/// <summary>
/// Gives the platform a supported lifetime while a user-initiated SSH session
/// is active. Android implements this with a foreground service; non-Android
/// hosts use the no-op implementation.
/// </summary>
public interface IActiveSessionLifetime
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Desktop/test-host implementation: no platform lifetime is required.</summary>
public sealed class NoOpActiveSessionLifetime : IActiveSessionLifetime
{
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
