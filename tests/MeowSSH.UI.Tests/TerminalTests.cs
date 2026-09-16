using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TerminalTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenSessionAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".xterm-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("prod-web-01");
        return page;
    }

    private static Task<string> ScreenTextAsync(IPage page) =>
        page.Locator(".xterm-rows").InnerTextAsync();

    [Fact]
    public async Task OpeningAHostShowsATerminalWithTheShellBanner()
    {
        var page = await OpenSessionAsync();

        await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("Last login");
        Assert.Contains("deploy@prod-web-01", await ScreenTextAsync(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedInputIsEchoedAndTheCommandRuns()
    {
        var page = await OpenSessionAsync();

        await page.Keyboard.TypeAsync("pwd");
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("/home/deploy", new() { Timeout = 10_000 });
        Assert.Contains("pwd", await ScreenTextAsync(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackspaceErasesACharacterRatherThanJustMovingTheCursor()
    {
        var page = await OpenSessionAsync();

        await page.Keyboard.TypeAsync("pwdx");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("/home/deploy", new() { Timeout = 10_000 });
        Assert.DoesNotContain("command not found", await ScreenTextAsync(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKeyBarContainsMobileShellKeys()
    {
        var page = await OpenSessionAsync();
        await Assertions.Expect(page.GetByTestId("keybar")).ToBeVisibleAsync();

        foreach (var key in new[]
                 {
                     "ctrl", "alt", "esc", "tab", "home", "end", "pgup", "pgdn",
                     "up", "down", "left", "right", "pipe", "tilde", "slash", "dash", "underscore"
                 })
        {
            await Assertions.Expect(page.GetByTestId($"key-{key}")).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task CtrlDoesNotStealTerminalFocusAndCtrlCStillWorks()
    {
        var page = await OpenSessionAsync();
        var input = page.Locator(".xterm-helper-textarea");
        await input.FocusAsync();
        await page.Keyboard.TypeAsync("partial-command");

        await page.GetByTestId("key-ctrl").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "true");

        var terminalStillFocused = await page.EvaluateAsync<bool>(
            "() => document.activeElement?.classList.contains('xterm-helper-textarea') === true");
        Assert.True(terminalStillFocused, "Tapping Ctrl moved focus away from xterm and would close the Android soft keyboard.");

        await page.Keyboard.PressAsync("c");

        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("^C", new() { Timeout = 10_000 });
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task CtrlCanBeCancelledByTappingItAgain()
    {
        var page = await OpenSessionAsync();

        await page.GetByTestId("key-ctrl").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByTestId("key-ctrl").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task AltLatchesWithoutStealingTerminalFocus()
    {
        var page = await OpenSessionAsync();
        var input = page.Locator(".xterm-helper-textarea");
        await input.FocusAsync();

        await page.GetByTestId("key-alt").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-alt")).ToHaveAttributeAsync("aria-pressed", "true");

        var terminalStillFocused = await page.EvaluateAsync<bool>(
            "() => document.activeElement?.classList.contains('xterm-helper-textarea') === true");
        Assert.True(terminalStillFocused, "Tapping Alt moved focus away from xterm.");

        await page.Keyboard.PressAsync("x");
        await Assertions.Expect(page.GetByTestId("key-alt")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task ToolbarKeysDoNotStealTerminalFocus()
    {
        var page = await OpenSessionAsync();
        var input = page.Locator(".xterm-helper-textarea");
        await input.FocusAsync();

        await page.GetByTestId("key-esc").ClickAsync();

        var terminalStillFocused = await page.EvaluateAsync<bool>(
            "() => document.activeElement?.classList.contains('xterm-helper-textarea') === true");
        Assert.True(terminalStillFocused, "A terminal toolbar key moved focus away from xterm.");
    }

    [Fact]
    public async Task TerminalRowsAllowNativeTextSelection()
    {
        var page = await OpenSessionAsync();

        var userSelect = await page.Locator(".xterm-rows").EvaluateAsync<string>(
            "element => getComputedStyle(element).userSelect");

        Assert.Equal("text", userSelect);
    }

    [Fact]
    public async Task TheRemotePtyIsToldTheSizeTheTerminalActuallyAchieved()
    {
        var page = await OpenSessionAsync();

        var input = page.Locator(".xterm-helper-textarea");
        await input.FocusAsync();
        await page.Keyboard.TypeAsync("tput cols");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("tput cols", new() { Timeout = 10_000 });

        // The echoed command proves that input reached the PTY, but its output is a
        // separate asynchronous terminal update. Wait for the numeric response itself
        // so slow CI runners cannot race the assertion between those two updates.
        await page.WaitForFunctionAsync(
            "() => /(?:^|\\n)\\s*\\d{2,3}\\s*(?:\\n|$)/.test(document.querySelector('.xterm-rows')?.innerText ?? '')",
            null,
            new() { Timeout = 10_000 });

        var screen = await ScreenTextAsync(page);
        var reported = System.Text.RegularExpressions.Regex.Matches(screen, @"^\s*(\d{2,3})\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(reported.Count > 0, $"Expected a column count on screen. Screen was:\n{screen}");
        var columns = int.Parse(reported[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(columns, 20, 300);
    }

    [Fact]
    public async Task ClosingTheSessionReturnsToTheHostList()
    {
        var page = await OpenSessionAsync();

        await page.GetByTestId("session-back").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheSessionScreenDoesNotScrollSideways()
    {
        var page = await OpenSessionAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "The session screen scrolls horizontally at 390px wide.");
    }

    [Fact]
    public async Task TheTerminalInputAsksForAKeyboardThatDoesNotCompose()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        var textarea = page.Locator(".xterm-helper-textarea");
        await Assertions.Expect(textarea).ToHaveAttributeAsync("type", "password");
        Assert.Null(await textarea.GetAttributeAsync("inputmode"));
        await Assertions.Expect(textarea).ToHaveAttributeAsync("autocomplete", "off");
        Assert.Equal("INPUT", await textarea.EvaluateAsync<string>("element => element.tagName"));
        await Assertions.Expect(textarea).ToHaveAttributeAsync("spellcheck", "false");
        await Assertions.Expect(textarea).ToHaveAttributeAsync("autocorrect", "off");
    }

    [Fact]
    public async Task SettingsTabPersistsTerminalZoom()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();

        var slider = page.GetByTestId("terminal-zoom");
        await slider.EvaluateAsync(
            @"element => {
                element.value = '140';
                element.dispatchEvent(new Event('input', { bubbles: true }));
            }");

        await Assertions.Expect(page.GetByTestId("terminal-zoom-value")).ToHaveTextAsync("140%");
        Assert.Equal("140", await page.EvaluateAsync<string>(
            "() => localStorage.getItem('meowssh.terminal.zoom')"));

        await page.ReloadAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal-zoom-value")).ToHaveTextAsync("140%");
    }

    [Fact]
    public async Task FilesTabCanBeSelected()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-files").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tab-files")).ToHaveAttributeAsync("aria-current", "page");
        await Assertions.Expect(page.GetByTestId("files-host-list")).ToBeVisibleAsync();
    }
}
