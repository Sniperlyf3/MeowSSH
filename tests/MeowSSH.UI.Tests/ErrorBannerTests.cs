using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The framework's unhandled-error bar.
/// </summary>
/// <remarks>
/// This exists because the Android build shipped without the rule that hides it.
/// Blazor puts the element in the page unconditionally and reveals it on an
/// exception; with no <c>display: none</c> default it sat in normal flow at the
/// foot of the document, so scrolling far enough told the user the app had
/// crashed when nothing had happened. The browser tests could not have caught
/// it, because the test host had no such element to style.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public class ErrorBannerTests(TestHostFixture fixture)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/?setup")]
    [InlineData("/?locked")]
    [InlineData("/?keys")]
    public async Task TheErrorBannerIsHiddenWhenNothingHasGoneWrong(string path)
    {
        var page = await fixture.NewPageAsync(path);

        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task TheErrorBannerIsPresentSoThatItCanBeRevealed()
    {
        // Hiding it by deleting it would also pass the test above, and would
        // leave a real failure with nothing to show for itself.
        var page = await fixture.NewPageAsync("/");

        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task ScrollingToTheFootOfThePageRevealsNothing()
    {
        // The symptom as it was actually reported: not an action, just scrolling
        // down far enough to reach a bar that was always there.
        var page = await fixture.NewPageAsync("/");
        await page.Mouse.WheelAsync(0, 5000);

        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task TheBannerCoversTheTabBarWhenItIsShown()
    {
        var page = await fixture.NewPageAsync("/");

        // Revealed the way Blazor reveals it, so the test proves the styling is
        // right rather than merely that the element is hidden.
        await page.EvalOnSelectorAsync("#blazor-error-ui", "el => el.style.display = 'block'");

        var banner = page.Locator("#blazor-error-ui");
        await Assertions.Expect(banner).ToBeVisibleAsync();

        var position = await banner.EvaluateAsync<string>("el => getComputedStyle(el).position");
        Assert.Equal("fixed", position);

        // A message saying the app has failed must not sit behind the navigation
        // it is telling you not to trust.
        var bannerZ = await banner.EvaluateAsync<int>("el => parseInt(getComputedStyle(el).zIndex) || 0");
        var tabBarZ = await page.Locator(".tabbar")
            .EvaluateAsync<int>("el => parseInt(getComputedStyle(el).zIndex) || 0");
        Assert.True(bannerZ > tabBarZ, $"error bar z-index {bannerZ} should sit above the tab bar's {tabBarZ}");
    }
}
