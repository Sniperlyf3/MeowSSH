using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class NavigationTests(TestHostFixture fixture)
{
    [Fact]
    public async Task PrimaryNavigationStaysCompactAndGroupsSpecialistTools()
    {
        var page = await fixture.NewPageAsync("/");

        await Assertions.Expect(page.Locator(".tabbar__tab")).ToHaveCountAsync(5);
        await Assertions.Expect(page.GetByTestId("tab-hosts")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-files")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-tools")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-keys")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-actions")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tab-monitoring")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tab-health")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tab-tailcat")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tab-backup")).ToHaveCountAsync(0);

        await page.GetByTestId("tab-tools").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tools-hub")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Automation & operations", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Network & data", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ToolPagesStayInsideToolsAndHaveOneTapBackToTheHub()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();

        await Assertions.Expect(page.GetByTestId("actions-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-tools")).ToHaveAttributeAsync("aria-current", "page");
        await Assertions.Expect(page.GetByTestId("tools-back")).ToBeVisibleAsync();

        await page.GetByTestId("tools-back").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tools-hub")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tools-back")).ToHaveCountAsync(0);
    }
}
