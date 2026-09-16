using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class CommandMonitoringTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenMonitoringAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-monitoring").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitoring-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task MonitorCanBeCreatedRunInspectedDisabledAndDeleted()
    {
        var page = await OpenMonitoringAsync();

        await page.GetByTestId("add-monitor").ClickAsync();
        await page.GetByTestId("monitor-name").FillAsync("Web health");
        await page.GetByTestId("monitor-host").SelectOptionAsync(new SelectOptionValue { Label = "prod-web-01 · deploy@10.4.2.11" });
        await page.GetByTestId("monitor-command").FillAsync("health-check");
        await page.GetByTestId("monitor-interval").FillAsync("300");
        await page.GetByTestId("save-monitor").ClickAsync();

        var card = page.Locator("[data-testid^='monitor-card-']").Filter(new LocatorFilterOptions { HasTextString = "Web health" });
        await Assertions.Expect(card).ToBeVisibleAsync();
        await Assertions.Expect(card).ToContainTextAsync("Never checked");

        await card.Locator("[data-testid^='run-monitor-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitor-message")).ToContainTextAsync("successfully");
        await Assertions.Expect(card).ToContainTextAsync("Healthy");

        await card.Locator("[data-testid^='history-monitor-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitor-history")).ToContainTextAsync("health-check on");
        await Assertions.Expect(page.GetByTestId("monitor-history")).ToContainTextAsync("healthy");
        await page.GetByTestId("close-monitor-history").ClickAsync();

        await card.Locator("[data-testid^='toggle-monitor-']").ClickAsync();
        await Assertions.Expect(card.Locator("[data-testid^='toggle-monitor-']")).ToHaveTextAsync("Enable");

        await card.Locator("[data-testid^='delete-monitor-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitoring-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FailingCommandIsPersistedAsFailureHistory()
    {
        var page = await OpenMonitoringAsync();

        await page.GetByTestId("add-monitor").ClickAsync();
        await page.GetByTestId("monitor-name").FillAsync("Broken service");
        await page.GetByTestId("monitor-host").SelectOptionAsync(new SelectOptionValue { Label = "prod-web-01 · deploy@10.4.2.11" });
        await page.GetByTestId("monitor-command").FillAsync("fail health-check");
        await page.GetByTestId("save-monitor").ClickAsync();

        var card = page.Locator("[data-testid^='monitor-card-']").Filter(new LocatorFilterOptions { HasTextString = "Broken service" });
        await card.Locator("[data-testid^='run-monitor-']").ClickAsync();
        await Assertions.Expect(card).ToContainTextAsync("Failing");

        await card.Locator("[data-testid^='history-monitor-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitor-history")).ToContainTextAsync("failed · exit 1");
        await Assertions.Expect(page.GetByTestId("monitor-history")).ToContainTextAsync("Alert: failure");
        await Assertions.Expect(page.GetByTestId("monitor-history")).ToContainTextAsync("fake failure");
    }
}
