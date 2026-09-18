using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class ManagedDerpMapUrlTests
{
    [Fact]
    public void ExplicitHttpsMapWins()
    {
        Assert.Equal(
            "https://relay.example/map.json",
            ManagedDerpMapUrl.Resolve(
                "https://relay.example/map.json",
                "https://api.example/"));
    }

    [Fact]
    public void LicensingApiProvidesManagedMapByDefault()
    {
        Assert.Equal(
            "https://api.example/base/v1/derp/map",
            ManagedDerpMapUrl.Resolve(
                null,
                "https://api.example/base/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://api.example/")]
    [InlineData("not-a-url")]
    public void MissingOrUnsafeConfigurationDoesNotFallBackToAPublicRelay(string? apiBaseUrl)
    {
        Assert.Equal(string.Empty, ManagedDerpMapUrl.Resolve(null, apiBaseUrl));
    }

    [Fact]
    public void UnsafeExplicitOverrideFallsBackToConfiguredManagedApi()
    {
        Assert.Equal(
            "https://api.example/v1/derp/map",
            ManagedDerpMapUrl.Resolve(
                "http://relay.example/map.json",
                "https://api.example/"));
    }
}
