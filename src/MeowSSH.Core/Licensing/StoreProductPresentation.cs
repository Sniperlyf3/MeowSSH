namespace MeowSSH.Core.Licensing;

public static class StoreProductPresentation
{
    public static string BuyerFacingName(string productId, string storeDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDisplayName);

        return string.Equals(productId, MeowSshProducts.ProLifetime, StringComparison.Ordinal)
            ? $"{storeDisplayName} — one-time purchase"
            : storeDisplayName;
    }
}
