using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Coverage for the Pro transfer queue: sequencing, progress/state, cancellation and the
/// FilesPage entry points that add to it. The free-tier gate itself (the queue throws for a
/// free user, and <c>TransferQueueContents</c> renders a "MeowSSH Pro required" card instead of
/// the list) is exercised in <c>MeowSSH.Core.Tests</c> and by code review against
/// <c>HostHealthDashboardPage</c>'s identical pattern -- <c>FakeEntitlementService</c> in
/// <c>TestHost</c> always grants Pro (every other Pro feature's Playwright coverage relies on the
/// same thing), so there is no way to reach the free-tier card from here without changing what
/// every other Pro-feature test assumes.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class TransferQueueTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenFilesAsync()
    {
        var page = await fixture.NewPageAsync("/?files");
        await Assertions.Expect(page.GetByTestId("filelist")).ToBeVisibleAsync();
        return page;
    }

    private static ILocator Entry(IPage page, string name) =>
        page.Locator($"[data-testid=file-entry][data-file-name='{name}']");

    private static ILocator Actions(IPage page, string name) =>
        page.Locator($"[data-testid=file-actions][data-file-name='{name}']");

    private static ILocator QueueItem(IPage page, string hostLabel) =>
        page.Locator("[data-testid=transfer-queue-item]").Filter(new() { HasTextString = hostLabel });

    private static async Task QueueDownloadAsync(IPage page, string fileName)
    {
        await Actions(page, fileName).ClickAsync();
        await page.GetByTestId("queue-download").ClickAsync();
    }

    [Fact]
    public async Task QueueingAnUploadConflictGoesThroughTheSameConfirmationAsAnImmediateUpload()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("queue-upload").ClickAsync();

        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToContainTextAsync("Replace start.sh?");

        await page.GetByTestId("replace-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("file-notice")).ToContainTextAsync("Queued upload of start.sh");

        await page.GetByTestId("open-transfer-queue").ClickAsync();
        var item = QueueItem(page, "prod-web-01");
        await Assertions.Expect(item).ToContainTextAsync("start.sh");
        await Assertions.Expect(item.GetByText("Completed")).ToBeVisibleAsync();

        await page.GetByTestId("transfer-queue-close").ClickAsync();
        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("view-text").ClickAsync();
        await Assertions.Expect(page.GetByTestId("text-content"))
            .ToHaveValueAsync("#!/bin/sh\necho uploaded replacement\n");
    }

    [Fact]
    public async Task CancellingAnUploadConflictLeavesNothingQueued()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("queue-upload").ClickAsync();
        await page.GetByTestId("cancel-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToHaveCountAsync(0);

        await page.GetByTestId("open-transfer-queue").ClickAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task QueuedDownloadsRunOneAtATimeInOrderAndProgressToCompleted()
    {
        var page = await OpenFilesAsync();

        // app.log is the fixture's deliberately slow (30s) download, so it is still running by
        // the time the second item is queued -- proving the second one waits rather than racing
        // it over the one fake SFTP session.
        await QueueDownloadAsync(page, "app.log");
        await QueueDownloadAsync(page, "start.sh");

        await page.GetByTestId("open-transfer-queue").ClickAsync();
        var appLog = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "app.log" });
        var startSh = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "start.sh" });

        await Assertions.Expect(appLog.GetByText("In progress")).ToBeVisibleAsync();
        await Assertions.Expect(startSh.GetByText("Queued")).ToBeVisibleAsync();

        // Cancelling the in-flight one frees the worker for the next -- and proves cancellation
        // actually reaches FakeSftpSession's Task.Delay rather than only hiding the UI, the same
        // property SftpActionTests already checks for an immediate (non-queued) download.
        await appLog.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(appLog.GetByText("Cancelled")).ToBeVisibleAsync();
        await Assertions.Expect(startSh.GetByText("Completed")).ToBeVisibleAsync();

        await page.GetByTestId("clear-finished-transfers").ClickAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-empty")).ToBeVisibleAsync();

        await Assertions.Expect(page.GetByTestId("transfer-history-list")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid=transfer-history-entry]")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task CancellingAQueuedDownloadBeforeItStartsNeverTouchesTheSession()
    {
        var page = await OpenFilesAsync();

        await QueueDownloadAsync(page, "app.log");
        await QueueDownloadAsync(page, "start.sh");
        await page.GetByTestId("open-transfer-queue").ClickAsync();

        var startSh = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "start.sh" });
        await Assertions.Expect(startSh.GetByText("Queued")).ToBeVisibleAsync();
        await startSh.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(startSh.GetByText("Cancelled")).ToBeVisibleAsync();

        // Clean up the still-running slow item so it does not linger past the test.
        var appLog = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "app.log" });
        await appLog.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(appLog.GetByText("Cancelled")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task RetryFromFilesRequeuesAgainstTheSameHostsLiveSession()
    {
        var page = await OpenFilesAsync();

        await QueueDownloadAsync(page, "app.log");
        await QueueDownloadAsync(page, "start.sh");
        await page.GetByTestId("open-transfer-queue").ClickAsync();

        var startSh = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "start.sh" });
        await startSh.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(startSh.GetByText("Cancelled")).ToBeVisibleAsync();

        // A live Sftp session for this same host is open right here, so retry is not just
        // present but actually enabled -- the negative case (disabled, with its explanation) is
        // covered separately from the host-independent Tools page, where no session is open.
        var retry = startSh.GetByTestId("retry-transfer-item");
        await Assertions.Expect(retry).ToBeEnabledAsync();
        await retry.ClickAsync();
        await Assertions.Expect(startSh.GetByText("Attempt 2")).ToBeVisibleAsync();

        // Cancelling app.log frees the worker; the retried start.sh (small, no artificial
        // delay) then runs to completion on its own almost immediately.
        var appLog = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "app.log" });
        await appLog.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(startSh.GetByText("Completed")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task RetryFromTheToolsPageIsDisabledAndExplainsWhyWithNoOpenSession()
    {
        var page = await OpenFilesAsync();

        await QueueDownloadAsync(page, "app.log");
        await QueueDownloadAsync(page, "start.sh");
        await page.GetByTestId("open-transfer-queue").ClickAsync();

        var startSh = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "start.sh" });
        await startSh.GetByTestId("cancel-transfer-item").ClickAsync();
        await Assertions.Expect(startSh.GetByText("Cancelled")).ToBeVisibleAsync();

        var appLog = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "app.log" });
        await appLog.GetByTestId("cancel-transfer-item").ClickAsync();

        await page.GetByTestId("transfer-queue-close").ClickAsync();
        await page.GetByTestId("files-back").ClickAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-transferqueue").ClickAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-page")).ToBeVisibleAsync();

        var toolsStartSh = QueueItem(page, "prod-web-01").Filter(new() { HasTextString = "start.sh" });
        var hint = toolsStartSh.GetByTestId("retry-transfer-item-hint");
        var retry = toolsStartSh.GetByTestId("retry-transfer-item");

        await Assertions.Expect(retry).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(hint).ToContainTextAsync("Open prod-web-01 to retry.");
        await Assertions.Expect(retry).ToHaveAttributeAsync("aria-describedby", await hint.GetAttributeAsync("id") ?? "");
    }
}
