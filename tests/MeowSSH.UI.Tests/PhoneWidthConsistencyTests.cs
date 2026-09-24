using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class PhoneWidthConsistencyTests(TestHostFixture fixture)
{
    private static readonly string[] PrimaryTabs =
    [
        "tab-hosts",
        "tab-files",
        "tab-tools",
        "tab-keys",
        "tab-settings",
    ];

    [Fact]
    public async Task PrimaryDestinationsDoNotOverflowAtPhoneWidth()
    {
        var page = await fixture.NewPageAsync();
        await page.SetViewportSizeAsync(390, 844);

        foreach (var tab in PrimaryTabs)
        {
            await page.GetByTestId(tab).ClickAsync();
            await Assertions.Expect(page.GetByTestId(tab)).ToHaveAttributeAsync("aria-current", "page");

            var overflows = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
            Assert.False(overflows, $"{tab} overflows horizontally at 390 px.");
        }
    }

    [Fact]
    public async Task PrimaryNavigationAndVisibleIconButtonsHaveComfortableTouchTargets()
    {
        var page = await fixture.NewPageAsync();
        await page.SetViewportSizeAsync(390, 844);

        foreach (var tab in PrimaryTabs)
        {
            await page.GetByTestId(tab).ClickAsync();
            // Measure the settled page, not the re-render the click started: a
            // loaded full-suite run once caught every tab mid-swap at 0x0,
            // which reads as "undersized" but is really "not laid out yet".
            await Assertions.Expect(page.GetByTestId(tab)).ToHaveAttributeAsync("aria-current", "page");

            var undersized = await page.Locator(".tabbar__tab:visible, .btn--icon:visible")
                .EvaluateAllAsync<string[]>(
                    "els => els.filter(el => { const r = el.getBoundingClientRect(); return r.width < 44 || r.height < 44; }).map(el => `${el.getAttribute('data-testid') || el.getAttribute('aria-label') || el.className}: ${Math.round(el.getBoundingClientRect().width)}x${Math.round(el.getBoundingClientRect().height)}`)");

            Assert.True(undersized.Length == 0,
                $"{tab} has touch targets below 44x44 CSS pixels: {string.Join(", ", undersized)}");
        }
    }

    [Fact]
    public async Task FormControlsMeetTheSameTouchHeightFloor()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.SetViewportSizeAsync(390, 844);

        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-password").ClickAsync();
        // The click swaps the flow picker for the editor. Without waiting, a
        // slow run measured the outgoing picker buttons at 0px and failed on
        // controls that were being removed, not on the form under test.
        await Assertions.Expect(page.GetByTestId("credential-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToHaveCountAsync(0);

        var undersized = await page.Locator("button:visible, input:visible, select:visible")
            .EvaluateAllAsync<string[]>(
                "els => els.filter(el => el.getBoundingClientRect().height < 44).map(el => `${el.getAttribute('data-testid') || el.getAttribute('aria-label') || el.tagName}: ${Math.round(el.getBoundingClientRect().height)}px`)");

        Assert.True(undersized.Length == 0,
            $"Visible credential controls below 44 CSS pixels: {string.Join(", ", undersized)}");
    }
}
