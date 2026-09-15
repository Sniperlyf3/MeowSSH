namespace MeowSSH.Core.Licensing;

public sealed record StoreProduct(
    string ProductId,
    string DisplayName,
    string FormattedPrice,
    string ProductType,
    string? OfferToken = null);

public sealed record StorePurchase(
    string ProductId,
    string PurchaseToken,
    bool IsAcknowledged,
    bool IsPending);

public interface IStorePurchaseService
{
    Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorePurchase>> GetPurchasesAsync(CancellationToken cancellationToken = default);
    Task<StorePurchase?> PurchaseAsync(string productId, CancellationToken cancellationToken = default);
}
