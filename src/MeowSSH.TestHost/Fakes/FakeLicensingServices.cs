using Microsoft.AspNetCore.Components;
using MeowSSH.Core.Licensing;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Grants a verified Pro entitlement, unless the page was opened with "free"
/// in the query string (e.g. <c>NewPageAsync("/?free")</c> or
/// <c>"/?tab-tools&amp;free"</c>), in which case it reports Free with no
/// grant at all. This piggybacks on the same string-Contains query parsing
/// Playground.razor already uses for "setup", "locked" and "multi" -- a
/// second, differently-shaped test-config mechanism (env var, header,
/// appsettings toggle) would be one more thing to keep in sync with how
/// tests actually drive the host, for no benefit over the pattern already
/// here.
/// </summary>
/// <remarks>
/// Read once per circuit, same as every other Playground.razor flag: a test
/// wanting the free-tier path opens its own <c>NewPageAsync("/?free...")</c>
/// rather than flipping an existing page's tier mid-session. Every one of
/// the 249 tests that predate this either passes no query string or passes
/// one without "free" in it, so <see cref="Current"/> still resolves to the
/// same verified-Pro snapshot they were written against.
/// </remarks>
public sealed class FakeEntitlementService : IEntitlementService
{
    private static readonly EntitlementSnapshot VerifiedPro = new(
        EntitlementTier.Pro,
        EntitlementSource.ServerVerifiedGooglePlay,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.MaxValue,
        "test-pro");

    private static readonly EntitlementSnapshot VerifiedProCloud = VerifiedPro with
    {
        Tier = EntitlementTier.ProCloud,
        GrantId = "test-pro-cloud",
    };

    private static readonly EntitlementSnapshot Unverified = EntitlementSnapshot.Free(DateTimeOffset.UnixEpoch);

    public EntitlementSnapshot Current { get; }

    /// <remarks>"procloud" selects the subscription tier cloud backup needs.</remarks>
    public FakeEntitlementService(NavigationManager navigation)
    {
        var query = new Uri(navigation.Uri).Query;
        Current = query.Contains("free", StringComparison.OrdinalIgnoreCase) ? Unverified
            : query.Contains("procloud", StringComparison.OrdinalIgnoreCase) ? VerifiedProCloud
            : VerifiedPro;
    }

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
        new StoreProduct(MeowSshProducts.ProCloud, "MeowSSH Pro Cloud — Monthly", "$2.99", "subs", MeowSshProducts.ProCloudMonthlyBasePlan),
        new StoreProduct(MeowSshProducts.ProCloud, "MeowSSH Pro Cloud — Yearly", "$24.99", "subs", MeowSshProducts.ProCloudYearlyBasePlan),
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

    public Task<StorePurchase?> PurchaseAsync(
        string productId,
        string? basePlanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        cancellationToken.ThrowIfCancellationRequested();
        StorePurchase? purchase = new(productId, "test-token", true, false);
        return Task.FromResult<StorePurchase?>(purchase);
    }
}
