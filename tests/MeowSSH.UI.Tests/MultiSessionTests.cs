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

    private static ILocator ActiveWorkspace(IPage page) =>
        page.Locator(".session-workspace.is-active");

    [Fact]
    public async Task OpeningAnotherHostKeepsTheFirstSessionAsATab()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        var workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session-tabs")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(1);

        await workspace.GetByTestId("new-session-tab").ClickAsync();
        await Assertions.Expect(page.GetByTestId("resume-sessions")).ToContainTextAsync("Return to open session");

        await page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" }).ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(2);
        await Assertions.Expect(workspace.GetByTestId("session")).ToContainTextAsync("build-runner");

        await workspace.GetByTestId("session-tab").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session")).ToContainTextAsync("prod-web-01");
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task ProBroadcastInputRequiresExplicitTargetSelectionAndArmedState()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        var workspace = ActiveWorkspace(page);
        await workspace.GetByTestId("new-session-tab").ClickAsync();
        await page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" }).ClickAsync();
        workspace = ActiveWorkspace(page);

        await workspace.GetByTestId("configure-broadcast").ClickAsync();
        var dialog = workspace.GetByTestId("broadcast-dialog");
        await Assertions.Expect(dialog).ToContainTextAsync("prod-web-01");
        await dialog.Locator("[data-testid^='broadcast-target-'] input[type='checkbox']").CheckAsync();
        await dialog.GetByTestId("arm-broadcast").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("broadcast-armed")).ToBeVisibleAsync();

        var input = workspace.Locator(".xterm-helper-textarea");
        await input.FocusAsync();
        await page.Keyboard.TypeAsync("pwd");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(workspace.Locator(".xterm-rows"))
            .ToContainTextAsync("/home/deploy", new() { Timeout = 10_000 });

        await workspace.GetByTestId("session-tab").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.Locator(".xterm-rows"))
            .ToContainTextAsync("/home/deploy", new() { Timeout = 10_000 });
        await Assertions.Expect(workspace.Locator(".xterm-rows")).ToContainTextAsync("pwd");
    }

    [Fact]
    public async Task OneConnectionCanSwitchBetweenTerminalAndFiles()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        var workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("terminal")).ToBeVisibleAsync();

        await workspace.GetByTestId("toggle-session-view").ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("files")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("session-tab").First).ToContainTextAsync("Files");

        await workspace.GetByTestId("toggle-session-view").ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("session-tab").First).ToContainTextAsync("SSH");
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task BackLeavesSessionsOpenAndTheyCanBeResumed()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await ActiveWorkspace(page).GetByTestId("session-back").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("resume-sessions")).ToContainTextAsync("Return to open session");

        await page.GetByTestId("resume-sessions").ClickAsync();
        var workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session-tabs")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("session")).ToContainTextAsync("prod-web-01");
    }

    [Fact]
    public async Task ClosingTabsSelectsAnotherThenReturnsToHostsAfterLastTab()
    {
        var page = await OpenMultiAsync();

        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        await ActiveWorkspace(page).GetByTestId("new-session-tab").ClickAsync();
        await page.GetByTestId("host-row").Filter(new() { HasText = "build-runner" }).ClickAsync();
        var workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(2);

        await workspace.GetByTestId("close-session-tab").Last.ClickAsync();
        workspace = ActiveWorkspace(page);
        await Assertions.Expect(workspace.GetByTestId("session-tab")).ToHaveCountAsync(1);
        await Assertions.Expect(workspace.GetByTestId("session")).ToContainTextAsync("prod-web-01");

        await workspace.GetByTestId("close-session-tab").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-tabs")).ToHaveCountAsync(0);
    }
}
