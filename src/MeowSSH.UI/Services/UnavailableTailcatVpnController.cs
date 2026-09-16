namespace MeowSSH.UI.Services;

/// <summary>
/// Used by builds that intentionally omit Android VpnService support. This keeps
/// the UI/service graph explicit while ensuring no VPN start capability exists.
/// </summary>
public sealed class UnavailableTailcatVpnController : ITailcatVpnController
{
    public bool IsAvailable => false;
    public TailcatVpnSnapshot Snapshot { get; } = new(false, null, []);

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Tailcat full-device VPN is not included in this MeowSSH build.");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
