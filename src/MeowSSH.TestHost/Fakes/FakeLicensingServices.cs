using MeowSSH.Core.Licensing;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeEntitlementService : IEntitlementService
{
    private static readonly EntitlementSnapshot VerifiedPro = new(
        EntitlementTier.Pro,
        EntitlementSource.ServerVerifiedGooglePlay,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.MaxValue,
        "test-pro");

    public EntitlementSnapshot Current => VerifiedPro;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool Has(PremiumFeature feature) =>
        EntitlementPolicy.Allows(Current, feature, DateTimeOffset.UtcNow);

    public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);
}

public sealed class FakeStorePurchaseService : IStorePurchaseService
{
    private static readonly IReadOnlyList<StoreProduct> Products =
    [
        new StoreProduct(MeowSshProducts.ProLifetime, "MeowSSH Pro", "$39.99", "inapp"),
    ];

    public Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Products);
    }

    public Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StorePurchase> purchases =
        [
            new StorePurchase(MeowSshProducts.ProLifetime, "test-token", true, false),
        ];
        return Task.FromResult(purchases);
    }

    public Task<StorePurchase?> PurchaseAsync(string productId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        cancellationToken.ThrowIfCancellationRequested();
        StorePurchase? purchase = new(productId, "test-token", true, false);
        return Task.FromResult<StorePurchase?>(purchase);
    }
}
