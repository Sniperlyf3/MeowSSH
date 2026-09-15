using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class AdvancedSftpTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenAdvancedAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-files").ClickAsync();
        await Assertions.Expect(page.GetByTestId("files-host-list")).ToBeVisibleAsync();
        await page.Locator("[data-testid='advanced-sftp-host'][data-host-label='prod-web-01']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-sftp-page")).ToBeVisibleAsync();
        return page;
    }

    private static ILocator FolderActions(IPage page, string name) =>
        page.Locator($"[data-testid='advanced-folder-actions'][data-file-name='{name}']");

    private static ILocator Folder(IPage page, string name) =>
        page.Locator($"[data-testid='advanced-folder'][data-file-name='{name}']");

    [Fact]
    public async Task NonEmptyDirectoryCanBeDeletedRecursivelyAfterSummaryConfirmation()
    {
        var page = await OpenAdvancedAsync();

        await FolderActions(page, ".config").ClickAsync();
        await page.GetByTestId("delete-folder-recursive").ClickAsync();

        var confirmation = page.GetByTestId("advanced-sftp-confirm");
        await Assertions.Expect(confirmation).ToContainTextAsync("Delete .config recursively");
        await Assertions.Expect(confirmation).ToContainTextAsync("1 files");
        await page.GetByTestId("confirm-recursive-delete").ClickAsync();

        await Assertions.Expect(page.GetByTestId("advanced-sftp-notice")).ToContainTextAsync("Deleted .config");
        await page.GetByTestId("advanced-sftp-notice").GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();
        await Assertions.Expect(Folder(page, ".config")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DirectoryCanBeDownloadedAsZipWithoutDeletingRemoteFolder()
    {
        var page = await OpenAdvancedAsync();

        await FolderActions(page, ".config").ClickAsync();
        await page.GetByTestId("download-folder-zip").ClickAsync();

        var confirmation = page.GetByTestId("advanced-sftp-confirm");
        await Assertions.Expect(confirmation).ToContainTextAsync("Download .config as ZIP");
        await Assertions.Expect(confirmation).ToContainTextAsync("214 B");
        await page.GetByTestId("confirm-folder-download").ClickAsync();

        await Assertions.Expect(page.GetByTestId("advanced-sftp-notice")).ToContainTextAsync("Saved ZIP to");
        await Assertions.Expect(Folder(page, ".config")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AdvancedBrowserCanNavigateAndReturnToFilesLanding()
    {
        var page = await OpenAdvancedAsync();

        await Folder(page, "releases").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-crumbs")).ToContainTextAsync("releases");
        await Assertions.Expect(Folder(page, "2026-09-11")).ToBeVisibleAsync();

        await page.GetByTestId("advanced-sftp-back").ClickAsync();
        await Assertions.Expect(page.GetByTestId("files-host-list")).ToBeVisibleAsync();
    }
}
