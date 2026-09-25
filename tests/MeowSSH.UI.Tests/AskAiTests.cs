using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class AskAiTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenSessionAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.Locator(".xterm-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".xterm-helper-textarea")).ToBeFocusedAsync();
        return page;
    }

    private static async Task RunAsync(IPage page, string command)
    {
        await page.Keyboard.TypeAsync(command);
        await page.Keyboard.PressAsync("Enter");
    }

    [Fact]
    public async Task WithNothingSelectedTheRecentScreenIsOfferedForReviewAndOnlyThatIsSent()
    {
        var page = await OpenSessionAsync("procloud");
        await RunAsync(page, "pwd");
        await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("/home/deploy");

        await page.GetByTestId("key-ai").ClickAsync();

        var input = page.GetByTestId("ai-input");
        await Assertions.Expect(input).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("/home/deploy"));
        await Assertions.Expect(page.GetByTestId("ai-privacy")).ToBeVisibleAsync();

        // Edited before sending: what goes out is the edited text, not the screen.
        await input.FillAsync("ERROR: disk quota exceeded on /srv");
        await page.GetByTestId("ai-explain").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-answer")).ToHaveTextAsync("You sent 34 characters starting \"ERROR: disk quota exceed\".");
    }

    [Fact]
    public async Task ASuggestedCommandIsTypedAtThePromptButNotRun()
    {
        var page = await OpenSessionAsync("procloud");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-question").FillAsync("how full are the disks");
        await page.GetByTestId("ai-command").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-command-text")).ToHaveTextAsync("df -h");
        await page.GetByTestId("ai-insert").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-panel")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("df -h");
        // The fake shell answers every unknown command with "command not
        // found" once Enter arrives; none must, because nothing pressed Enter.
        await page.WaitForTimeoutAsync(300);
        Assert.DoesNotContain("command not found", await page.Locator(".xterm-rows").InnerTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultiLineCommandIsNeverOfferedForInsertion()
    {
        // Pasting it would run every line but the last without a keypress.
        var page = await OpenSessionAsync("procloud&aimultiline");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-question").FillAsync("clear the app cache");
        await page.GetByTestId("ai-command").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-multiline")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("ai-insert")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task InsertingDisarmsAnArmedModifier()
    {
        // Left armed, Alt would prefix the inserted command with ESC.
        var page = await OpenSessionAsync("procloud");
        await page.GetByTestId("key-alt").ClickAsync();
        await Assertions.Expect(page.GetByTestId("key-alt")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-question").FillAsync("how full are the disks");
        await page.GetByTestId("ai-command").ClickAsync();
        await page.GetByTestId("ai-insert").ClickAsync();

        await Assertions.Expect(page.GetByTestId("key-alt")).ToHaveAttributeAsync("aria-pressed", "false");
        await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("df -h");
        Assert.DoesNotContain("^[", await page.Locator(".xterm-rows").InnerTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandNeedsAQuestionAndExplainNeedsText()
    {
        var page = await OpenSessionAsync("procloud");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-input").FillAsync("");

        await Assertions.Expect(page.GetByTestId("ai-explain")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("ai-command")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task ProLocalSeesTheUpsellAndCannotSendAnything()
    {
        var page = await OpenSessionAsync("");
        await page.GetByTestId("key-ai").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("ai-input")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("ai-explain")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AServiceRefusalIsShownInWords()
    {
        var page = await OpenSessionAsync("procloud&aifails");
        await RunAsync(page, "whoami");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-explain").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-error")).ToContainTextAsync("allowance");
        await Assertions.Expect(page.GetByTestId("ai-answer")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ClosingThePanelReturnsToTheTerminal()
    {
        var page = await OpenSessionAsync("procloud");
        await page.GetByTestId("key-ai").ClickAsync();

        await page.GetByTestId("ai-close").ClickAsync();

        await Assertions.Expect(page.GetByTestId("ai-panel")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
    }
}
