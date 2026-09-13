using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TerminalTests(TestHostFixture fixture)
{
    /// <summary>Opens a session on the first host and waits for the shell banner.</summary>
    private async Task<IPage> OpenSessionAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".xterm-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("prod-web-01");
        return page;
    }

    /// <summary>Reads what the terminal is actually displaying.</summary>
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

        // "/home/deploy" appears only if the command actually ran. Asserting on
        // "deploy" alone would pass without any input reaching the shell at all,
        // because the prompt already reads deploy@prod-web-01.
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

        // If backspace only moved the cursor without erasing, the shell would
        // have received "pwdx" and reported it as not found.
        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("/home/deploy", new() { Timeout = 10_000 });
        Assert.DoesNotContain("command not found", await ScreenTextAsync(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKeyBarSendsKeysAPhoneKeyboardDoesNotHave()
    {
        var page = await OpenSessionAsync();
        await Assertions.Expect(page.GetByTestId("keybar")).ToBeVisibleAsync();

        foreach (var key in new[] { "ctrl", "esc", "tab", "up", "down", "left", "right" })
            await Assertions.Expect(page.GetByTestId($"key-{key}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CtrlLatchesAndTurnsTheNextKeystrokeIntoAControlCode()
    {
        // A touch screen cannot hold one key while pressing another, so Ctrl
        // latches. Ctrl then C must reach the shell as 0x03, which the fake
        // answers with "^C" -- proof the byte arrived, not just the letter.
        var page = await OpenSessionAsync();
        await page.Keyboard.TypeAsync("partial-command");

        await page.GetByTestId("key-ctrl").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "true");

        await page.Locator(".xterm-helper-textarea").PressAsync("c");

        await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("^C", new() { Timeout = 10_000 });
        // The latch is one-shot: leaving it armed would send the next ordinary
        // keystroke somewhere the user did not intend.
        await Assertions.Expect(page.GetByTestId("key-ctrl")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task TheRemotePtyIsToldTheSizeTheTerminalActuallyAchieved()
    {
        // Not the size the app guessed. A mismatch makes every full-screen
        // program draw wrong until something happens to resize it.
        var page = await OpenSessionAsync();

        await page.Keyboard.TypeAsync("tput cols");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".xterm-rows"))
            .ToContainTextAsync("tput cols", new() { Timeout = 10_000 });

        // The fake echoes back the width the component told it, so this compares
        // what the pty was told against what the terminal actually measured.
        var measured = await page.EvaluateAsync<int>(
            "() => document.querySelector('.terminal__surface').__cols ?? 0");
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
        // A phone keyboard's predictive text holds the word being typed as
        // uncommitted composition, draws it over the terminal, and leaves the
        // real caret one word behind until something commits it. The hidden
        // capture element is password-style so Android treats it as literal
        // input rather than a normal predictive text field.
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        var textarea = page.Locator(".xterm-helper-textarea");
        await Assertions.Expect(textarea).ToHaveAttributeAsync("type", "password");
        Assert.Null(await textarea.GetAttributeAsync("inputmode"));
        await Assertions.Expect(textarea).ToHaveAttributeAsync("autocomplete", "off");

        // The hidden capture element is deliberately a password-style input.
        // Android/Samsung treats that as literal text and disables predictive
        // composition, while xterm still receives ordinary input events.
        Assert.Equal("INPUT", await textarea.EvaluateAsync<string>("element => element.tagName"));

        // The ones xterm sets itself, asserted so that an upgrade dropping them
        // is noticed here rather than on a phone.
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
