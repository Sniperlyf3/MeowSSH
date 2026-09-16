using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests.Licensing;

public sealed class StoreProductPresentationTests
{
    [Fact]
    public void LifetimeProductIsExplicitlyOneTime()
    {
        var name = StoreProductPresentation.BuyerFacingName(
            MeowSshProducts.ProLifetime,
            "MeowSSH Pro");

        Assert.Equal("MeowSSH Pro — one-time purchase", name);
        Assert.Contains("one-time", name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MeowSshProducts.ProCloud, "MeowSSH Pro Cloud")]
    [InlineData("future_product", "Future product")]
    public void OtherProductsKeepStoreDisplayName(string productId, string displayName)
    {
        Assert.Equal(displayName, StoreProductPresentation.BuyerFacingName(productId, displayName));
    }
}
