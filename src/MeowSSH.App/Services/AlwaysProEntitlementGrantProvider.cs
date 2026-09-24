using MeowSSH.Core.Licensing;

namespace MeowSSH.App.Services;

/// <summary>
/// Test-only stand-in for <see cref="LicensingApiGrantProvider"/>, compiled in
/// only when <c>ForceProEntitlementForTesting</c> is passed at build time (see
/// MeowSSH.App.csproj). LicensingBuildConfig fails closed to Free when
/// LicensingApiBaseUrl is blank, which is correct for a real build but leaves
/// no way to exercise Pro-gated UI before the licensing API is deployed. This
/// grants the highest tier unconditionally so a sideloaded build can be used
/// to test Pro functionality against that not-yet-running API.
/// </summary>
internal sealed class AlwaysProEntitlementGrantProvider(TimeProvider timeProvider) : IEntitlementGrantProvider
{
    private EntitlementSnapshot Snapshot() => new(
        EntitlementTier.ProCloud,
        EntitlementSource.Promotional,
        timeProvider.GetUtcNow(),
        ValidUntilUtc: null,
        GrantId: "local-test-build");

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot());

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot());
}
