using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class HeartbeatMonitorsTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-heartbeats").ClickAsync();
        await Assertions.Expect(page.GetByTestId("heartbeats-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task AddingAMonitorShowsTheExactCronLineToUse()
    {
        var page = await OpenAsync("procloud");
        await Assertions.Expect(page.GetByTestId("heartbeat-empty")).ToBeVisibleAsync();

        await page.GetByTestId("heartbeat-name").FillAsync("nightly backup");
        await page.GetByTestId("heartbeat-period").SelectOptionAsync("1440");
        await page.GetByTestId("heartbeat-create").ClickAsync();

        var monitor = page.GetByTestId("heartbeat-monitor");
        await Assertions.Expect(monitor).ToHaveCountAsync(1);
        await Assertions.Expect(monitor.GetByTestId("heartbeat-state")).ToHaveTextAsync("Waiting for first ping");
        await Assertions.Expect(monitor.GetByTestId("heartbeat-command"))
            .ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^curl -fsS -m 10 --retry 3 https://api\.meowssh\.test/v1/ping/\S+ > /dev/null$"));
        // The failure variant names the real URL, not a placeholder.
        await Assertions.Expect(monitor.GetByTestId("heartbeat-fail-hint"))
            .ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"\|\| curl -fsS https://api\.meowssh\.test/v1/ping/\S+/fail$"));
    }

    [Fact]
    public async Task ADownMonitorSaysSoAndOneFromAnotherPhoneExplainsItsMissingUrl()
    {
        var page = await OpenAsync("procloud&heartbeatseeded");

        var monitors = page.GetByTestId("heartbeat-monitor");
        await Assertions.Expect(monitors).ToHaveCountAsync(2);
        await Assertions.Expect(monitors.Nth(0).GetByTestId("heartbeat-state")).ToHaveTextAsync("Down");
        await Assertions.Expect(monitors.Nth(1).GetByTestId("heartbeat-no-token")).ToBeVisibleAsync();
        await Assertions.Expect(monitors.Nth(1).GetByTestId("heartbeat-command")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DeletingRemovesTheMonitor()
    {
        var page = await OpenAsync("procloud&heartbeatseeded");
        await Assertions.Expect(page.GetByTestId("heartbeat-monitor")).ToHaveCountAsync(2);

        await page.GetByTestId("heartbeat-monitor").Nth(0).GetByTestId("heartbeat-delete").ClickAsync();

        await Assertions.Expect(page.GetByTestId("heartbeat-monitor")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("heartbeat-message")).ToContainTextAsync("no longer works");
    }

    [Fact]
    public async Task WithoutProCloudThereIsNoCreateFormButExistingMonitorsCanStillBeDeleted()
    {
        var page = await OpenAsync("heartbeatseeded");

        await Assertions.Expect(page.GetByTestId("heartbeat-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("heartbeat-create")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("heartbeat-delete")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task AMonitorNeedsAName()
    {
        var page = await OpenAsync("procloud");

        await Assertions.Expect(page.GetByTestId("heartbeat-create")).ToBeDisabledAsync();
        await page.GetByTestId("heartbeat-name").FillAsync("x");
        await Assertions.Expect(page.GetByTestId("heartbeat-create")).ToBeEnabledAsync();
    }
}
