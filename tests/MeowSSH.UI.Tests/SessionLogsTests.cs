using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class SessionLogsTests(TestHostFixture fixture)
{
    [Fact]
    public async Task ProUserCanEnableRecordingAndOpenHistory()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();

        await page.GetByTestId("session-log-auto-record").CheckAsync();
        await Assertions.Expect(page.GetByTestId("session-log-settings-message"))
            .ToContainTextAsync("record output locally");

        await page.GetByTestId("open-session-logs").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-logs-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-logs-empty")).ToBeVisibleAsync();

        await page.GetByTestId("session-logs-back").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
        await page.GetByTestId("session-log-auto-record").UncheckAsync();
        await Assertions.Expect(page.GetByTestId("session-log-settings-message"))
            .ToContainTextAsync("recording is off");
    }
}
