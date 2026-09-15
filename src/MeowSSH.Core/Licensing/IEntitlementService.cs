namespace MeowSSH.Core.Licensing;

public interface IEntitlementService
{
    EntitlementSnapshot Current { get; }
    event EventHandler? Changed;

    bool Has(PremiumFeature feature);

    Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default);
}

public interface IEntitlementGrantProvider
{
    Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default);
}

public interface IEntitlementCache
{
    Task<EntitlementSnapshot?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EntitlementSnapshot entitlement, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
