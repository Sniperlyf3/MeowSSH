using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class TailcatNavigationUxTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTailcatAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task TailcatToolsAreGroupedByUserIntentWithoutRemovingDirectSections()
    {
        var page = await OpenTailcatAsync();
        var groups = page.GetByTestId("tailcat-intent-groups");

        await Assertions.Expect(groups).ToContainTextAsync("Connect");
        await Assertions.Expect(groups).ToContainTextAsync("Devices & keys");
        await Assertions.Expect(groups).ToContainTextAsync("Network & VPN");
        await Assertions.Expect(groups).ToContainTextAsync("Transfers");
        await Assertions.Expect(groups).ToContainTextAsync("Diagnostics");

        foreach (var section in new[] { "workspaces", "phone", "connect", "vpn", "transfers", "address", "keys", "diagnostics" })
            await Assertions.Expect(page.GetByTestId($"tailcat-section-{section}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TailcatIntentNavigationDoesNotOverflowAtPhoneWidth()
    {
        var page = await OpenTailcatAsync();
        await page.SetViewportSizeAsync(390, 844);

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "Tailcat intent navigation scrolls the page horizontally at phone width.");
    }
}
