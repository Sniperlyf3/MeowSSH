using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TerminalUxTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenAppearanceAsync()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("theme-grid")).ToHaveCountAsync(0);
        await page.GetByTestId("open-appearance-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("appearance-settings-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task AppearanceUsesDedicatedPreviewPageWithoutClutteringSettingsRoot()
    {
        var page = await OpenAppearanceAsync();

        await Assertions.Expect(page.GetByTestId("theme-meow-dark")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("theme-solarized-dark")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("theme-solarized-light")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("theme-dracula")).ToBeVisibleAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflows, "Appearance settings should not overflow a phone-width viewport.");

        await page.GetByTestId("appearance-back").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("open-appearance-settings")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SettingsPersistThemeCursorAndScrollback()
    {
        var page = await OpenAppearanceAsync();

        await page.GetByTestId("theme-dracula").ClickAsync();
        await page.GetByTestId("terminal-cursor-style").SelectOptionAsync("bar");
        await page.GetByTestId("terminal-scrollback").SelectOptionAsync("20000");
        await page.GetByTestId("terminal-cursor-blink").UncheckAsync();

        await page.WaitForFunctionAsync("() => localStorage.getItem('meowssh.terminal.theme') === 'dracula'");
        await page.WaitForFunctionAsync("() => localStorage.getItem('meowssh.terminal.cursorStyle') === 'bar'");
        await page.WaitForFunctionAsync("() => localStorage.getItem('meowssh.terminal.scrollback') === '20000'");
        await page.WaitForFunctionAsync("() => localStorage.getItem('meowssh.terminal.cursorBlink') === 'false'");

        await Assertions.Expect(page.GetByTestId("theme-dracula")).ToHaveAttributeAsync("aria-checked", "true");

        await page.ReloadAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-appearance-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("theme-dracula")).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(page.GetByTestId("terminal-cursor-style")).ToHaveValueAsync("bar");
        await Assertions.Expect(page.GetByTestId("terminal-scrollback")).ToHaveValueAsync("20000");
        await Assertions.Expect(page.GetByTestId("terminal-cursor-blink")).Not.ToBeCheckedAsync();
    }

    [Fact]
    public async Task ResetRestoresTerminalDefaults()
    {
        var page = await fixture.NewPageAsync("/");
        await page.EvaluateAsync("() => localStorage.setItem('meowssh.terminal.theme', 'dracula')");
        await page.EvaluateAsync("() => localStorage.setItem('meowssh.terminal.zoom', '150')");
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-appearance-settings").ClickAsync();

        await page.GetByTestId("terminal-settings-reset").ClickAsync();

        await Assertions.Expect(page.GetByTestId("theme-meow-dark")).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(page.GetByTestId("terminal-zoom-value")).ToHaveTextAsync("100%");
        Assert.Null(await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('meowssh.terminal.theme')"));
        Assert.Null(await page.EvaluateAsync<string?>(
            "() => localStorage.getItem('meowssh.terminal.zoom')"));
    }

    [Fact]
    public async Task MobileToolbarIncludesClipboardEditingAndFunctionKeys()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        foreach (var key in new[] { "copy", "paste", "clear", "insert", "delete", "f1", "f5", "f12" })
            await Assertions.Expect(page.GetByTestId($"key-{key}")).ToBeVisibleAsync();
    }
}
