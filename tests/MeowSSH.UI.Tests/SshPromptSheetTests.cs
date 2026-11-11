using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The handshake's questions as the user meets them: the host-key, password
/// and keyboard-interactive sheets, each answered and declined by tapping.
/// </summary>
/// <remarks>
/// FakeSshEngine asks them through the real prompt plumbing when the page is
/// opened with "hostkeyprompt", "passwordprompt" or "challengeprompt" in the
/// query string. InteractiveSshPromptsTests covers what the engine gets back;
/// this covers the sheets themselves.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class SshPromptSheetTests(TestHostFixture fixture)
{
    private static ILocator BuildRunner(IPage page) => page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" });

    [Fact]
    public async Task TheHostKeySheetShowsTheFingerprintAndTrustingConnects()
    {
        var page = await fixture.NewPageAsync("?multi&hostkeyprompt");
        await BuildRunner(page).ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-key-prompt")).ToContainTextAsync("build.internal");
        await Assertions.Expect(page.GetByTestId("host-key-fingerprint")).ToHaveTextAsync("SHA256:FakeTestHostKeyFingerprintForTheUiSuite0000");
        await page.GetByTestId("trust-host-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-key-prompt")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RejectingTheHostKeyDoesNotConnect()
    {
        var page = await fixture.NewPageAsync("?multi&hostkeyprompt");
        await BuildRunner(page).ClickAsync();
        await page.GetByTestId("reject-host-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("connect-error")).ToContainTextAsync("Host not trusted yet");
        await Assertions.Expect(page.GetByTestId("terminal")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ThePasswordSheetSignsInWithEnter()
    {
        var page = await fixture.NewPageAsync("?multi&passwordprompt");
        await BuildRunner(page).ClickAsync();

        await Assertions.Expect(page.GetByTestId("password-prompt")).ToContainTextAsync("ci@build.internal");
        var input = page.GetByTestId("password-input");
        await Assertions.Expect(input).ToHaveAttributeAsync("type", "password");
        await input.FillAsync("correct horse");
        await input.PressAsync("Enter");

        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedAndCancellingStops()
    {
        var page = await fixture.NewPageAsync("?multi&passwordprompt");
        await BuildRunner(page).ClickAsync();
        await page.GetByTestId("password-input").FillAsync("wrong");
        await page.GetByTestId("submit-password").ClickAsync();
        await Assertions.Expect(page.GetByTestId("connect-error")).ToContainTextAsync("Could not sign in");
        await page.GetByTestId("dismiss-error").ClickAsync();

        await BuildRunner(page).ClickAsync();
        await page.GetByTestId("cancel-password").ClickAsync();
        await Assertions.Expect(page.GetByTestId("password-prompt")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("terminal")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheChallengeSheetShowsTheServersWordsAndEchoesAVisibleCode()
    {
        var page = await fixture.NewPageAsync("?multi&challengeprompt");
        await BuildRunner(page).ClickAsync();

        var sheet = page.GetByTestId("challenge-prompt");
        await Assertions.Expect(sheet).ToContainTextAsync("Two-step sign-in");
        await Assertions.Expect(sheet).ToContainTextAsync("Enter the code from your authenticator.");
        var answer = page.GetByTestId("challenge-answer-0");
        // Echo is on for this question: a code read off a device, not a secret.
        await Assertions.Expect(answer).ToHaveAttributeAsync("type", "text");
        await answer.FillAsync("123456");
        await page.GetByTestId("submit-challenge").ClickAsync();

        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CancellingTheChallengeDoesNotConnect()
    {
        var page = await fixture.NewPageAsync("?multi&challengeprompt");
        await BuildRunner(page).ClickAsync();
        await page.GetByTestId("cancel-challenge").ClickAsync();

        await Assertions.Expect(page.GetByTestId("challenge-prompt")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("terminal")).ToHaveCountAsync(0);
    }
}
