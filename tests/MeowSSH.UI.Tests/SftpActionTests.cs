using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class SftpActionTests(TestHostFixture fixture)
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

    [Fact]
    public async Task UploadingAnExistingFileRequiresAnExplicitDecision()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("upload-file").ClickAsync();

        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToContainTextAsync("Replace start.sh?");
        await Assertions.Expect(Entry(page, "start.sh")).ToContainTextAsync("402 B");
    }

    [Fact]
    public async Task CancellingAnUploadConflictPreservesTheRemoteFile()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("upload-file").ClickAsync();
        await page.GetByTestId("cancel-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToHaveCountAsync(0);

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("view-text").ClickAsync();
        await Assertions.Expect(page.GetByTestId("text-content"))
            .ToHaveValueAsync("#!/bin/sh\necho hello from MeowSSH\n");
    }

    [Fact]
    public async Task ReplacingAnUploadConflictOverwritesOnlyAfterConfirmation()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("upload-file").ClickAsync();
        await page.GetByTestId("replace-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToHaveCountAsync(0);

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("view-text").ClickAsync();
        await Assertions.Expect(page.GetByTestId("text-content"))
            .ToHaveValueAsync("#!/bin/sh\necho uploaded replacement\n");
    }

    [Fact]
    public async Task FileActionsCanDeleteAFile()
    {
        var page = await OpenFilesAsync();

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("delete-file").ClickAsync();
        await Assertions.Expect(page.GetByTestId("delete-confirm")).ToBeVisibleAsync();
        await page.GetByTestId("confirm-delete").ClickAsync();

        await Assertions.Expect(Entry(page, "start.sh")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task PermissionsCanBeChangedUsingOctalMode()
    {
        var page = await OpenFilesAsync();

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("change-permissions").ClickAsync();
        await page.GetByTestId("permission-mode").FillAsync("0644");
        await page.GetByTestId("save-permissions").ClickAsync();

        await Assertions.Expect(Entry(page, "start.sh")).ToContainTextAsync("rw-r--r--");
    }

    [Fact]
    public async Task SmallUtf8FilesCanBeViewedAndEdited()
    {
        var page = await OpenFilesAsync();

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("view-text").ClickAsync();
        await Assertions.Expect(page.GetByTestId("text-viewer")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("text-content")).ToHaveValueAsync("#!/bin/sh\necho hello from MeowSSH\n");

        await page.GetByTestId("text-content").FillAsync("#!/bin/sh\necho changed\n");
        await page.GetByTestId("save-text").ClickAsync();
        await Assertions.Expect(page.GetByTestId("file-notice")).ToContainTextAsync("Saved");
    }

    [Fact]
    public async Task DownloadProducesAVisibleCompletionNotice()
    {
        var page = await OpenFilesAsync();

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("download-file").ClickAsync();

        await Assertions.Expect(page.GetByTestId("file-notice")).ToContainTextAsync("Saved to");
    }

    [Fact]
    public async Task LargeFileDoesNotTryToOpenAsText()
    {
        var page = await OpenFilesAsync();

        await Actions(page, "app.log").ClickAsync();
        await page.GetByTestId("view-text").ClickAsync();

        await Assertions.Expect(page.GetByTestId("files-error"))
            .ToContainTextAsync("too large to open as text");
    }
}
