using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class InteractionBudgetTests(TestHostFixture fixture)
{
    [Fact]
    public async Task SavedHostOpensWithOneActivationFromHosts()
    {
        var page = await fixture.NewPageAsync("/?multi");
        var host = page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" });

        await host.ClickAsync();

        var workspace = page.Locator(".session-workspace.is-active");
        await Assertions.Expect(workspace.GetByTestId("session")).ToContainTextAsync("prod-web-01");
        await Assertions.Expect(page.GetByTestId("host-list")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task SpecialistToolNeedsOneActivationAfterReachingTools()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tools-hub")).ToBeVisibleAsync();

        await page.GetByTestId("open-tool-actions").ClickAsync();

        await Assertions.Expect(page.GetByTestId("actions-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tab-tools")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task SessionViewChangesWithOneStableSelectorInteraction()
    {
        var page = await fixture.NewPageAsync("/?multi");
        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        var workspace = page.Locator(".session-workspace.is-active");
        var selector = workspace.GetByTestId("session-view-selector");

        await Assertions.Expect(selector).ToHaveCountAsync(1);
        await Assertions.Expect(workspace.GetByTestId("toggle-session-view")).ToHaveCountAsync(0);
        await Assertions.Expect(workspace.GetByTestId("open-forwards")).ToHaveCountAsync(0);

        await selector.SelectOptionAsync("Files");

        workspace = page.Locator(".session-workspace.is-active");
        await Assertions.Expect(workspace.GetByTestId("files")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("session-view-selector")).ToHaveValueAsync("Files");
    }
}
