using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class MultiSessionTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenMultiAsync()
    {
        var page = await fixture.NewPageAsync("/?multi");
        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task OpeningAnotherHostKeepsTheFirstSessionAsATab()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-tabs")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(1);

        await page.GetByTestId("new-session-tab").ClickAsync();
        await Assertions.Expect(page.GetByTestId("resume-sessions")).ToContainTextAsync("1 open session");

        await page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("build-runner");

        await page.GetByTestId("session-tab").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("prod-web-01");
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task OneConnectionCanSwitchBetweenTerminalAndFiles()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        await page.GetByTestId("toggle-session-view").ClickAsync();
        await Assertions.Expect(page.GetByTestId("files")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-tab").First).ToContainTextAsync("Files");

        await page.GetByTestId("toggle-session-view").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-tab").First).ToContainTextAsync("SSH");
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task BackLeavesSessionsOpenAndTheyCanBeResumed()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await page.GetByTestId("session-back").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("resume-sessions")).ToContainTextAsync("Return to open session");

        await page.GetByTestId("resume-sessions").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-tabs")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("prod-web-01");
    }

    [Fact]
    public async Task ClosingTabsSelectsAnotherThenReturnsToHostsAfterLastTab()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await page.GetByTestId("new-session-tab").ClickAsync();
        await page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(2);

        await page.GetByTestId("close-session-tab").Last.ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-tab")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("session")).ToContainTextAsync("prod-web-01");

        await page.GetByTestId("close-session-tab").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-tabs")).ToHaveCountAsync(0);
    }
}
