using MeowSSH.Core.Licensing;

namespace MeowSSH.UI.Services;

/// <summary>
/// Enforces the paid Tailcat VPN boundary independently of the UI. A modified
/// view cannot start the Android VPN unless the current verified entitlement
/// allows it. Stopping is always permitted so access changes never strand a
/// running VPN session.
/// </summary>
public sealed class EntitlementTailcatVpnController(
    ITailcatVpnController inner,
    IEntitlementService entitlements) : ITailcatVpnController
{
    public bool IsAvailable => inner.IsAvailable;
    public TailcatVpnSnapshot Snapshot => inner.Snapshot;

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!inner.IsAvailable)
            throw new InvalidOperationException("Tailcat full-device VPN is not included in this MeowSSH build.");
        if (!entitlements.Has(PremiumFeature.TailcatFullDeviceVpn))
            throw new InvalidOperationException("MeowSSH Pro is required for Tailcat full-device VPN routing.");

        return inner.StartAsync(request, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        inner.StopAsync(cancellationToken);
}
