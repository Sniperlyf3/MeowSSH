using Android.BillingClient.Api;
using Android.Content;
using MeowSSH.App.Services;
using MeowSSH.Core.Licensing;
using Microsoft.Maui.ApplicationModel;
using static Android.BillingClient.Api.BillingClient;

namespace MeowSSH.App;

public sealed class GooglePlayPurchaseService : Java.Lang.Object, IStorePurchaseService, IPurchasesUpdatedListener
{
    private readonly BillingClient _client;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private TaskCompletionSource<(BillingResult Result, IList<Purchase>? Purchases)>? _pendingPurchase;

    public GooglePlayPurchaseService()
    {
        var pending = PendingPurchasesParams.NewBuilder()
            .EnableOneTimeProducts()
            .EnablePrepaidPlans()
            .Build();

        _client = BillingClient.NewBuilder(global::Android.App.Application.Context)
            .SetListener(this)
            .EnablePendingPurchases(pending)
            .Build();
    }

    public async Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var products = new List<StoreProduct>();
        products.AddRange(await QueryProductsAsync(
            new[] { MeowSshProducts.ProLifetime }, ProductType.Inapp, cancellationToken).ConfigureAwait(false));

        // Keep Pro Cloud invisible until the actual cloud product exists. A Play
        // Console subscription being active must never be enough to make an
        // unfinished tier purchasable from the client.
        if (CommercialBuildConfig.ProCloudSalesEnabled)
        {
            products.AddRange(await QueryProductsAsync(
                new[] { MeowSshProducts.ProCloud }, ProductType.Subs, cancellationToken).ConfigureAwait(false));
        }

        return products;
    }

    public async Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var purchases = new List<StorePurchase>();
        purchases.AddRange(await QueryPurchasesAsync(ProductType.Inapp, cancellationToken).ConfigureAwait(false));
        // Existing/test subscription purchases still need to be restored even
        // while new Pro Cloud sales are disabled.
        purchases.AddRange(await QueryPurchasesAsync(ProductType.Subs, cancellationToken).ConfigureAwait(false));
        return purchases;
    }

    public async Task<StorePurchase?> PurchaseAsync(
        string productId,
        string? basePlanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        if (string.Equals(productId, MeowSshProducts.ProCloud, StringComparison.Ordinal) &&
            !CommercialBuildConfig.ProCloudSalesEnabled)
        {
            throw new InvalidOperationException(
                "MeowSSH Pro Cloud is not available for purchase until cloud sync is released.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        if (_pendingPurchase is { Task.IsCompleted: false })
            throw new InvalidOperationException("A Google Play purchase is already in progress.");

        var productType = ProductTypeFor(productId);
        var query = QueryProductDetailsParams.Product.NewBuilder()
            .SetProductType(productType)
            .SetProductId(productId)
            .Build();
        var detailsResult = await _client.QueryProductDetailsAsync(
            QueryProductDetailsParams.NewBuilder().SetProductList(new[] { query }).Build()).ConfigureAwait(false);
        var details = detailsResult.ProductDetailsList.FirstOrDefault()
            ?? throw new InvalidOperationException($"Google Play did not return product details for '{productId}'.");

        var detailsBuilder = BillingFlowParams.ProductDetailsParams.NewBuilder().SetProductDetails(details);
        if (productType == ProductType.Subs)
        {
            if (string.IsNullOrWhiteSpace(basePlanId))
                throw new InvalidOperationException("A subscription base plan must be selected before purchase.");

            var offer = PreferredOffer(details, basePlanId)
                ?? throw new InvalidOperationException($"Google Play did not return an eligible offer for base plan '{basePlanId}'.");
            detailsBuilder.SetOfferToken(offer.OfferToken);
        }

        var flow = BillingFlowParams.NewBuilder()
            .SetProductDetailsParamsList(new[] { detailsBuilder.Build() })
            .Build();

        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("The Android activity is unavailable for the Google Play purchase flow.");
        _pendingPurchase = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => _pendingPurchase.TrySetCanceled(cancellationToken));

        var launch = _client.LaunchBillingFlow(activity, flow);
        EnsureOk(launch, "start purchase");

        var completed = await _pendingPurchase.Task.ConfigureAwait(false);
        EnsureOk(completed.Result, "complete purchase");
        var purchase = completed.Purchases?.FirstOrDefault(p => p.Products.Contains(productId));
        return purchase is null ? null : MapPurchase(purchase, productId);
    }

    public void OnPurchasesUpdated(BillingResult p0, IList<Purchase>? p1)
    {
        _pendingPurchase?.TrySetResult((p0, p1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connectionGate.Dispose();
            _client.EndConnection();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsReady) return;

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsReady) return;

            var tcs = new TaskCompletionSource<BillingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            _client.StartConnection(
                result => tcs.TrySetResult(result),
                () => { });
            EnsureOk(await tcs.Task.ConfigureAwait(false), "connect to Google Play Billing");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<IReadOnlyList<StorePurchase>> QueryPurchasesAsync(string productType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _client.QueryPurchasesAsync(
            QueryPurchasesParams.NewBuilder().SetProductType(productType).Build()).ConfigureAwait(false);
        EnsureOk(result.Result, "query purchases");

        return result.Purchases
            .SelectMany(p => p.Products.Select(productId => MapPurchase(p, productId)))
            .ToArray();
    }

    private async Task<IReadOnlyList<StoreProduct>> QueryProductsAsync(
        IEnumerable<string> productIds,
        string productType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestProducts = productIds
            .Select(id => QueryProductDetailsParams.Product.NewBuilder()
                .SetProductType(productType)
                .SetProductId(id)
                .Build())
            .ToArray();
        var result = await _client.QueryProductDetailsAsync(
            QueryProductDetailsParams.NewBuilder().SetProductList(requestProducts).Build()).ConfigureAwait(false);

        if (productType == ProductType.Inapp)
        {
            return result.ProductDetailsList
                .Select(details => new StoreProduct(
                    details.ProductId,
                    details.Name,
                    details.GetOneTimePurchaseOfferDetails()?.FormattedPrice ?? string.Empty,
                    details.ProductType))
                .ToArray();
        }

        var subscriptions = new List<StoreProduct>();
        foreach (var details in result.ProductDetailsList)
        {
            AddBasePlan(details, MeowSshProducts.ProCloudMonthlyBasePlan, "Monthly", subscriptions);
            AddBasePlan(details, MeowSshProducts.ProCloudYearlyBasePlan, "Yearly", subscriptions);
        }
        return subscriptions;
    }

    private static void AddBasePlan(
        ProductDetails details,
        string basePlanId,
        string label,
        List<StoreProduct> products)
    {
        var offer = PreferredOffer(details, basePlanId);
        if (offer is null) return;

        var phases = offer.PricingPhases?.PricingPhaseList;
        var paidPhase = phases?.LastOrDefault(static phase => phase.PriceAmountMicros > 0)
            ?? phases?.LastOrDefault();
        products.Add(new StoreProduct(
            details.ProductId,
            $"{details.Name} — {label}",
            paidPhase?.FormattedPrice ?? string.Empty,
            details.ProductType,
            basePlanId));
    }

    private static ProductDetails.SubscriptionOfferDetails? PreferredOffer(ProductDetails details, string basePlanId) =>
        details.GetSubscriptionOfferDetails()?
            .Where(offer => string.Equals(offer.BasePlanId, basePlanId, StringComparison.Ordinal))
            .OrderBy(offer => string.IsNullOrWhiteSpace(offer.OfferId) ? 0 : 1)
            .FirstOrDefault();

    private static string ProductTypeFor(string productId) => productId switch
    {
        MeowSshProducts.ProLifetime => ProductType.Inapp,
        MeowSshProducts.ProCloud => ProductType.Subs,
        _ => throw new ArgumentOutOfRangeException(nameof(productId), productId, "Unknown MeowSSH Google Play product."),
    };

    private static StorePurchase MapPurchase(Purchase purchase, string productId) => new(
        productId,
        purchase.PurchaseToken,
        purchase.IsAcknowledged,
        purchase.PurchaseState == PurchaseState.Pending);

    private static void EnsureOk(BillingResult result, string operation)
    {
        if (result.ResponseCode == BillingResponseCode.Ok) return;
        throw new InvalidOperationException($"Google Play Billing could not {operation}: {result.ResponseCode} ({result.DebugMessage}).");
    }
}
