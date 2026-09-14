using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TerminalUxTests(TestHostFixture fixture)
{
    [Fact]
    public async Task SettingsPersistThemeCursorAndScrollback()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();

        await page.GetByTestId("terminal-theme").SelectOptionAsync("dracula");
        await page.GetByTestId("terminal-cursor-style").SelectOptionAsync("bar");
        await page.GetByTestId("terminal-scrollback").SelectOptionAsync("20000");
        await page.GetByTestId("terminal-cursor-blink").UncheckAsync();

        Assert.Equal("dracula", await page.EvaluateAsync<string>(
            "() => localStorage.getItem('meowssh.terminal.theme')"));
        Assert.Equal("bar", await page.EvaluateAsync<string>(
            "() => localStorage.getItem('meowssh.terminal.cursorStyle')"));
        Assert.Equal("20000", await page.EvaluateAsync<string>(
            "() => localStorage.getItem('meowssh.terminal.scrollback')"));
        Assert.Equal("false", await page.EvaluateAsync<string>(
            "() => localStorage.getItem('meowssh.terminal.cursorBlink')"));

        await page.ReloadAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal-theme")).ToHaveValueAsync("dracula");
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

        await page.GetByTestId("terminal-settings-reset").ClickAsync();

        await Assertions.Expect(page.GetByTestId("terminal-theme")).ToHaveValueAsync("meow-dark");
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
