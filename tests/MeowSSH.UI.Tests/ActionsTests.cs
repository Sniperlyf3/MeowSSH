using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class ActionsTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenActionsAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-actions").ClickAsync();
        await Assertions.Expect(page.GetByTestId("actions-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task ActionCanBeCreatedRunEditedAndDeleted()
    {
        var page = await OpenActionsAsync();

        await page.GetByTestId("add-action").ClickAsync();
        await page.GetByTestId("action-name").FillAsync("Check uptime");
        await page.GetByTestId("action-command").FillAsync("uptime");
        await SelectHostAsync(page, "prod-web-01");
        await page.GetByTestId("save-action").ClickAsync();

        var card = page.Locator("[data-testid^='action-card-']").Filter(new LocatorFilterOptions { HasTextString = "Check uptime" });
        await Assertions.Expect(card).ToBeVisibleAsync();
        await Assertions.Expect(card).ToContainTextAsync("prod-web-01");

        await card.Locator("[data-testid^='run-action-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("action-results")).ToContainTextAsync("Completed successfully");
        await Assertions.Expect(page.GetByTestId("action-results")).ToContainTextAsync("uptime on");

        await card.Locator("[data-testid^='edit-action-']").ClickAsync();
        await page.GetByTestId("action-name").FillAsync("Check load");
        await page.GetByTestId("action-command").FillAsync("cat /proc/loadavg");
        await page.GetByTestId("save-action").ClickAsync();

        card = page.Locator("[data-testid^='action-card-']").Filter(new LocatorFilterOptions { HasTextString = "Check load" });
        await Assertions.Expect(card).ToBeVisibleAsync();
        await card.Locator("[data-testid^='delete-action-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("actions-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ProActionRunsAgainstMultipleHostsAndShowsPerHostResults()
    {
        var page = await OpenActionsAsync();

        await page.GetByTestId("add-action").ClickAsync();
        await page.GetByTestId("action-name").FillAsync("Fleet hostname");
        await page.GetByTestId("action-command").FillAsync("hostname");
        await SelectHostAsync(page, "prod-web-01");
        await SelectHostAsync(page, "build-runner");
        await Assertions.Expect(page.GetByTestId("multi-host-pro-note")).ToBeVisibleAsync();
        await page.GetByTestId("save-action").ClickAsync();

        var card = page.Locator("[data-testid^='action-card-']").Filter(new LocatorFilterOptions { HasTextString = "Fleet hostname" });
        await card.Locator("[data-testid^='run-action-']").ClickAsync();

        await Assertions.Expect(page.GetByTestId("action-results")).ToContainTextAsync("prod-web-01");
        await Assertions.Expect(page.GetByTestId("action-results")).ToContainTextAsync("build-runner");
        await Assertions.Expect(page.Locator("[data-testid^='action-result-']")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task ProParameterizedActionPromptsForEphemeralValues()
    {
        var page = await OpenActionsAsync();

        await page.GetByTestId("add-action").ClickAsync();
        await page.GetByTestId("action-name").FillAsync("Deploy environment");
        await page.GetByTestId("action-command").FillAsync("echo {{environment}}");
        await SelectHostAsync(page, "prod-web-01");
        await page.GetByTestId("save-action").ClickAsync();

        var card = page.Locator("[data-testid^='action-card-']").Filter(new LocatorFilterOptions { HasTextString = "Deploy environment" });
        await Assertions.Expect(card).ToContainTextAsync("{{environment}}");
        await Assertions.Expect(card.GetByTestId("action-variable-summary")).ToContainTextAsync("environment");
        await Assertions.Expect(card.Locator("[data-testid^='run-action-']")).ToHaveCountAsync(0);

        await card.Locator("[data-testid^='run-parameterized-action-']").ClickAsync();
        await page.GetByTestId("action-variable-environment").FillAsync("production west");
        await page.GetByTestId("confirm-parameterized-action").ClickAsync();

        var results = card.GetByTestId("parameterized-action-results");
        await Assertions.Expect(results).ToContainTextAsync("Completed successfully");
        await Assertions.Expect(results).ToContainTextAsync("echo 'production west' on");
        await Assertions.Expect(card).ToContainTextAsync("{{environment}}");
    }

    private static async Task SelectHostAsync(IPage page, string label)
    {
        var host = page.Locator("label.action-host").Filter(new LocatorFilterOptions { HasTextString = label });
        await host.Locator("input[type='checkbox']").CheckAsync();
    }
}
