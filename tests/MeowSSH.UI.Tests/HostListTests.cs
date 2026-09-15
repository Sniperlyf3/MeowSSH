using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class HostListTests(TestHostFixture fixture)
{
    [Fact]
    public async Task ListsEverySavedHost()
    {
        var page = await fixture.NewPageAsync();
        await Assertions.Expect(page.GetByTestId("host-row")).ToHaveCountAsync(6);
    }

    [Fact]
    public async Task ConnectedHostsAreGroupedAboveTheRest()
    {
        // A live session is what the user is most likely returning to, so it must
        // not be buried in an alphabetical list.
        var page = await fixture.NewPageAsync();
        var headings = page.Locator(".section-label");

        await Assertions.Expect(headings.First).ToContainTextAsync("Active sessions");
        await Assertions.Expect(page.GetByTestId("host-row").First).ToContainTextAsync("prod-web-01");
    }

    [Fact]
    public async Task AFailedHostShowsWhyItFailed()
    {
        var page = await fixture.NewPageAsync();
        var failed = page.GetByTestId("host-row").Filter(new() { HasText = "staging-db-replica" });

        await Assertions.Expect(failed.Locator(".host__reason")).ToHaveTextAsync("Host key changed");
        await Assertions.Expect(failed.Locator(".badge--danger")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task NothingOverflowsAtPhoneWidth()
    {
        // The app ships to phones only, so a horizontal scrollbar is a defect, not
        // a cosmetic issue -- it means a control has been pushed off screen.
        var page = await fixture.NewPageAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "The page scrolls horizontally at 390px wide.");
    }

    [Fact]
    public async Task EveryNavigationTabIsReachable()
    {
        var page = await fixture.NewPageAsync();

        foreach (var tab in new[] { "hosts", "files", "tailcat", "keys", "settings" })
            await Assertions.Expect(page.GetByTestId($"tab-{tab}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task IconsRenderAtAReadableSizeRatherThanFillingTheirButton()
    {
        // Regression guard: an <svg> carrying only a viewBox resolves to 100%
        // width, which once made the fingerprint icon swallow its entire button.
        var page = await fixture.NewPageAsync("/?locked");
        var icon = page.GetByTestId("unlock-biometric").Locator(".icon");
        await Assertions.Expect(icon).ToBeVisibleAsync();

        var box = await icon.BoundingBoxAsync();

        Assert.NotNull(box);
        Assert.InRange(box!.Width, 8, 32);
    }
}
