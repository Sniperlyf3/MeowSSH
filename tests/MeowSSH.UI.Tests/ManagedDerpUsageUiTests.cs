using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class ManagedDerpUsageUiTests(TestHostFixture fixture)
{
    [Fact]
    public async Task KeyManagerShowsServerAuthoritativeManagedRelayUsage()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-section-keys").ClickAsync();

        await Assertions.Expect(page.GetByTestId("managed-derp-usage-card")).ToContainTextAsync(
            "Direct Tailcat traffic and other relays are not counted");
        await page.GetByTestId("managed-derp-refresh-usage").ClickAsync();

        await Assertions.Expect(page.GetByTestId("managed-derp-usage-summary")).ToContainTextAsync("384 KiB");
        await Assertions.Expect(page.GetByTestId("managed-derp-usage-summary")).ToContainTextAsync("1 GiB");
        await Assertions.Expect(page.GetByTestId("managed-derp-usage-details")).ToContainTextAsync("Sent 256 KiB");
        await Assertions.Expect(page.GetByTestId("managed-derp-usage-details")).ToContainTextAsync("received 128 KiB");
        await Assertions.Expect(page.GetByTestId("managed-derp-usage-details")).ToContainTextAsync("Free");
    }
}
