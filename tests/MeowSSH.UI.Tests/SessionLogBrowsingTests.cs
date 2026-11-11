using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// A recorded session, end to end in the browser: turn recording on, use a
/// terminal, then find, read, export and delete what was recorded.
/// </summary>
/// <remarks>
/// The test host records with the real FileSessionLogService behind the real
/// SessionLoggingConnectionEngine; only the shell is fake (FakeSshShell).
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class SessionLogBrowsingTests(TestHostFixture fixture)
{
    /// <summary>Records one session on <paramref name="host"/> that printed the working directory.</summary>
    private static async Task RecordASessionAsync(IPage page, string host)
    {
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-session-log-settings").ClickAsync();
        await page.GetByTestId("session-log-auto-record").CheckAsync();
        await page.GetByTestId("session-log-settings-back").ClickAsync();
        await page.GetByTestId("tab-hosts").ClickAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = host }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await page.GetByTestId("terminal").ClickAsync();
        await page.Keyboard.TypeAsync("pwd");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("terminal")).ToContainTextAsync("/home/deploy");
        await page.GetByTestId("close-session-tab").ClickAsync();
    }

    private static async Task OpenLogsAsync(IPage page)
    {
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-session-log-settings").ClickAsync();
        await page.GetByTestId("open-session-logs").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-logs-page")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ARecordedSessionCanBeFoundReadAndExported()
    {
        var page = await fixture.NewPageAsync("?multi");
        await RecordASessionAsync(page, "prod-web-01");
        await OpenLogsAsync(page);

        var list = page.GetByTestId("session-log-list");
        await Assertions.Expect(list).ToContainTextAsync("prod-web-01");

        // Search finds it by what the terminal printed, and not by nonsense.
        await page.GetByTestId("session-log-search").FillAsync("no-such-output-anywhere");
        await page.GetByTestId("session-log-search-run").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-logs-empty")).ToBeVisibleAsync();
        await page.GetByTestId("session-log-search").FillAsync("/home/deploy");
        await page.GetByTestId("session-log-search-run").ClickAsync();
        await Assertions.Expect(list).ToContainTextAsync("prod-web-01");

        await list.Locator("[data-testid^='view-session-log-']").First.ClickAsync();
        var detail = page.GetByTestId("session-log-detail");
        await Assertions.Expect(detail).ToContainTextAsync("prod-web-01");
        // Output is recorded; typed input deliberately is not.
        await Assertions.Expect(page.GetByTestId("session-log-content")).ToContainTextAsync("/home/deploy");

        await page.GetByTestId("session-log-export").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-log-message")).ToContainTextAsync("Exported session log to");

        await page.GetByTestId("session-log-close-detail").ClickAsync();
        await Assertions.Expect(detail).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ALogCanBeDeletedFromItsDetailAndTheRestCleared()
    {
        var page = await fixture.NewPageAsync("?multi");
        await RecordASessionAsync(page, "prod-web-01");
        await RecordASessionAsync(page, "bastion");
        await OpenLogsAsync(page);

        var cards = page.GetByTestId("session-log-list").Locator("article.session-log-card");
        await Assertions.Expect(cards).ToHaveCountAsync(2);
        await cards.Filter(new() { HasText = "bastion" }).Locator("[data-testid^='view-session-log-']").ClickAsync();
        await page.GetByTestId("session-log-delete-selected").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-log-message")).ToHaveTextAsync("Session log deleted.");
        await Assertions.Expect(cards).ToHaveCountAsync(1);
        await Assertions.Expect(cards).ToContainTextAsync("prod-web-01");

        await page.GetByTestId("session-logs-clear").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-log-message")).ToHaveTextAsync("Completed session logs cleared.");
        await Assertions.Expect(page.GetByTestId("session-logs-empty")).ToBeVisibleAsync();
    }
}
