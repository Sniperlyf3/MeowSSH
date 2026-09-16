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
        var page = await fixture.NewPageAsync();
        var headings = page.Locator(".section-label");

        await Assertions.Expect(headings.First).ToContainTextAsync("Active sessions");
        await Assertions.Expect(page.GetByTestId("host-row").First).ToContainTextAsync("prod-web-01");
    }

    [Fact]
    public async Task SearchFiltersHostsWithoutLeavingThePage()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("search").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-search-controls")).ToBeVisibleAsync();

        await page.GetByTestId("host-search").FillAsync("prod-web-01");
        await Assertions.Expect(page.GetByTestId("host-row")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("host-row")).ToContainTextAsync("prod-web-01");
    }

    [Fact]
    public async Task SearchShowsAnExplicitEmptyState()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("search").ClickAsync();
        await page.GetByTestId("host-search").FillAsync("definitely-not-a-real-host");

        await Assertions.Expect(page.GetByTestId("host-search-empty")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-row")).ToHaveCountAsync(0);
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
        var page = await fixture.NewPageAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "The page scrolls horizontally at 390px wide.");
    }

    [Fact]
    public async Task PrimaryNavigationHasFiveStableDestinations()
    {
        var page = await fixture.NewPageAsync();

        foreach (var tab in new[] { "hosts", "files", "tools", "keys", "settings" })
            await Assertions.Expect(page.GetByTestId($"tab-{tab}")).ToBeVisibleAsync();

        await Assertions.Expect(page.Locator("nav[aria-label='Main'] button")).ToHaveCountAsync(5);
        foreach (var legacy in new[] { "actions", "monitoring", "health", "tailcat", "backup" })
            await Assertions.Expect(page.GetByTestId($"tab-{legacy}")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ToolsProvidesOneTapAccessToSpecialistDestinations()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("tab-tools").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tools-hub")).ToBeVisibleAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-tools")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task IconsRenderAtAReadableSizeRatherThanFillingTheirButton()
    {
        var page = await fixture.NewPageAsync("/?locked");
        var icon = page.GetByTestId("unlock-biometric").Locator(".icon");
        await Assertions.Expect(icon).ToBeVisibleAsync();

        var box = await icon.BoundingBoxAsync();

        Assert.NotNull(box);
        Assert.InRange(box!.Width, 8, 32);
    }
}
