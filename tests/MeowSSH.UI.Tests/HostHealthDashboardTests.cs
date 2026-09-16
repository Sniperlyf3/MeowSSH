using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class HostHealthDashboardTests(TestHostFixture fixture)
{
    [Fact]
    public async Task ProUserRunsOnDemandFleetHealthCheck()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("tab-health").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-health-dashboard")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-health-results")).ToHaveCountAsync(0);

        await page.GetByTestId("refresh-host-health").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-health-results")).ToBeVisibleAsync();
        var results = page.GetByTestId("host-health-result");
        await Assertions.Expect(results.First).ToContainTextAsync("Linux");
        await Assertions.Expect(results.First).ToContainTextAsync("up 2 hours");
        await Assertions.Expect(results.First).ToContainTextAsync("42%");
        await Assertions.Expect(page.GetByTestId("host-health-message")).ToContainTextAsync("responded over SSH");
    }
}
