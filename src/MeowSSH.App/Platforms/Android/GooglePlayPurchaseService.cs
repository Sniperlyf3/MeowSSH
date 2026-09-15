using Android.BillingClient.Api;
using Android.Content;
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
        products.AddRange(await QueryProductsAsync(
            new[] { MeowSshProducts.ProCloudMonthly, MeowSshProducts.ProCloudYearly }, ProductType.Subs, cancellationToken).ConfigureAwait(false));
        return products;
    }

    public async Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var purchases = new List<StorePurchase>();
        purchases.AddRange(await QueryPurchasesAsync(ProductType.Inapp, cancellationToken).ConfigureAwait(false));
        purchases.AddRange(await QueryPurchasesAsync(ProductType.Subs, cancellationToken).ConfigureAwait(false));
        return purchases;
    }

    public async Task<StorePurchase?> PurchaseAsync(string productId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
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
            var offerToken = details.GetSubscriptionOfferDetails()?.FirstOrDefault()?.OfferToken;
            if (!string.IsNullOrWhiteSpace(offerToken)) detailsBuilder.SetOfferToken(offerToken);
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

    public void OnPurchasesUpdated(BillingResult billingResult, IList<Purchase>? purchases)
    {
        _pendingPurchase?.TrySetResult((billingResult, purchases));
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

        return result.ProductDetailsList.Select(details =>
        {
            var offerToken = productType == ProductType.Subs
                ? details.GetSubscriptionOfferDetails()?.FirstOrDefault()?.OfferToken
                : null;
            return new StoreProduct(details.ProductId, details.Name, string.Empty, details.ProductType, offerToken);
        }).ToArray();
    }

    private static string ProductTypeFor(string productId) => productId switch
    {
        MeowSshProducts.ProLifetime => ProductType.Inapp,
        MeowSshProducts.ProCloudMonthly or MeowSshProducts.ProCloudYearly => ProductType.Subs,
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
