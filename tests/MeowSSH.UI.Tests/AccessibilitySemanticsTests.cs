using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class AccessibilitySemanticsTests(TestHostFixture fixture)
{
    [Fact]
    public async Task PrimaryNavigationExposesCurrentDestination()
    {
        var page = await fixture.NewPageAsync();

        foreach (var tab in new[] { "hosts", "files", "tools", "keys", "settings" })
        {
            await page.GetByTestId($"tab-{tab}").ClickAsync();
            await Assertions.Expect(page.GetByTestId($"tab-{tab}")).ToHaveAttributeAsync("aria-current", "page");
        }
    }

    [Fact]
    public async Task TailcatTabsOwnTheActiveTabPanel()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();

        var phoneTab = page.GetByTestId("tailcat-section-phone");
        await Assertions.Expect(phoneTab).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(phoneTab).ToHaveAttributeAsync("aria-controls", "tailcat-panel-phone");
        await Assertions.Expect(page.GetByTestId("tailcat-active-panel")).ToHaveAttributeAsync("aria-labelledby", "tailcat-tab-phone");

        var diagnosticsTab = page.GetByTestId("tailcat-section-diagnostics");
        await diagnosticsTab.ClickAsync();

        await Assertions.Expect(phoneTab).ToHaveAttributeAsync("aria-selected", "false");
        await Assertions.Expect(diagnosticsTab).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.GetByTestId("tailcat-active-panel")).ToHaveAttributeAsync("id", "tailcat-panel-diagnostics");
        await Assertions.Expect(page.GetByTestId("tailcat-active-panel")).ToHaveAttributeAsync("aria-labelledby", "tailcat-tab-diagnostics");
    }

    [Fact]
    public async Task ToolBackControlHasAnExplicitAccessibleName()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tools-back")).ToHaveAttributeAsync("aria-label", "Back to Tools");
    }
}
