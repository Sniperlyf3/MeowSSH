using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// PremiumFeature.PremiumCustomization. Assertions read the colour the
/// terminal actually renders, not which card looks selected: the Pro gate
/// lives in terminal.js where the theme is applied, and that is the part a
/// lapsed or refunded purchase must not get around.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class PremiumCustomizationTests(TestHostFixture fixture)
{
    private const string MeowDark = "rgb(11, 14, 19)";
    private const string Nord = "rgb(46, 52, 64)";
    private const string ProductionRed = "rgb(42, 15, 18)";

    private static async Task OpenAppearanceAsync(IPage page)
    {
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-appearance-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("appearance-settings-page")).ToBeVisibleAsync();
    }

    private static async Task<string> OpenFirstHostTerminalBackgroundAsync(IPage page)
    {
        await page.GetByTestId("host-row").First.ClickAsync();
        var viewport = page.GetByTestId("terminal").Locator(".xterm-viewport");
        await Assertions.Expect(viewport).ToBeVisibleAsync();
        return await viewport.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
    }

    [Fact]
    public async Task ProCanChooseAPremiumThemeAndTheTerminalRendersIt()
    {
        var page = await fixture.NewPageAsync("/");
        await OpenAppearanceAsync(page);

        await page.GetByTestId("theme-nord").ClickAsync();
        await Assertions.Expect(page.GetByTestId("theme-nord")).ToHaveAttributeAsync("aria-checked", "true");
        await page.GetByTestId("appearance-back").ClickAsync();
        await page.GetByTestId("tab-hosts").ClickAsync();

        Assert.Equal(Nord, await OpenFirstHostTerminalBackgroundAsync(page));
    }

    [Fact]
    public async Task AFreeTapOnAPremiumThemeExplainsItAndStoresNothing()
    {
        var page = await fixture.NewPageAsync("/?free");
        await OpenAppearanceAsync(page);

        await page.GetByTestId("theme-nord").ClickAsync();

        await Assertions.Expect(page.GetByTestId("appearance-pro-hint")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("theme-meow-dark")).ToHaveAttributeAsync("aria-checked", "true");
        Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('meowssh.terminal.theme')"));
    }

    [Fact]
    public async Task ASavedPremiumThemeRendersAsTheDefaultOnceProLapses()
    {
        // A Pro user chose Nord, then the purchase was refunded: the choice is
        // kept (it returns if Pro does) but must not keep rendering.
        var page = await fixture.NewPageAsync("/?free");
        await page.EvaluateAsync("() => localStorage.setItem('meowssh.terminal.theme', 'nord')");
        await OpenAppearanceAsync(page);

        await Assertions.Expect(page.GetByTestId("appearance-theme-paused")).ToBeVisibleAsync();
        await page.GetByTestId("appearance-back").ClickAsync();
        await page.GetByTestId("tab-hosts").ClickAsync();

        Assert.Equal(MeowDark, await OpenFirstHostTerminalBackgroundAsync(page));
        Assert.Equal("nord", await page.EvaluateAsync<string>("() => localStorage.getItem('meowssh.terminal.theme')"));
    }

    [Fact]
    public async Task TheCustomThemeEditorsColoursReachTheTerminal()
    {
        var page = await fixture.NewPageAsync("/");
        await OpenAppearanceAsync(page);

        await page.GetByTestId("theme-custom").ClickAsync();
        await Assertions.Expect(page.GetByTestId("custom-theme-editor")).ToBeVisibleAsync();
        await page.GetByTestId("custom-theme-background").FillAsync("#123456");
        await page.WaitForFunctionAsync("() => (localStorage.getItem('meowssh.terminal.customTheme') ?? '').includes('#123456')");

        await page.GetByTestId("appearance-back").ClickAsync();
        await page.GetByTestId("tab-hosts").ClickAsync();

        Assert.Equal("rgb(18, 52, 86)", await OpenFirstHostTerminalBackgroundAsync(page));
    }

    [Fact]
    public async Task APerHostThemeIsSavedAndUsedForThatHostsSessions()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("manage-host").First.ClickAsync();
        await page.GetByTestId("edit-host").ClickAsync();
        await page.GetByTestId("host-organisation").Locator("summary").ClickAsync();

        await page.GetByTestId("host-terminal-theme").SelectOptionAsync("production-red");
        await page.GetByTestId("save-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);

        Assert.Equal(ProductionRed, await OpenFirstHostTerminalBackgroundAsync(page));
    }

    [Fact]
    public async Task WithoutProASavedHostThemeIsIgnoredAndItsPickerIsDisabled()
    {
        // The seeded host names Dracula, a free palette: the per-host theme is
        // itself the Pro feature, so even a free palette must not apply.
        var page = await fixture.NewPageAsync("/?free&hosttheme");

        Assert.Equal(MeowDark, await OpenFirstHostTerminalBackgroundAsync(page));

        var editor = await fixture.NewPageAsync("/?free&hosttheme");
        await editor.GetByTestId("manage-host").First.ClickAsync();
        await editor.GetByTestId("edit-host").ClickAsync();
        var picker = editor.GetByTestId("host-terminal-theme");
        await Assertions.Expect(picker).ToBeDisabledAsync();
        // Shown, not erased: the stored choice survives for when Pro returns.
        await Assertions.Expect(picker).ToHaveValueAsync("dracula");
    }
}
