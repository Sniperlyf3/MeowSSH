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
        var more = page.Locator("[data-testid='files-host-more'][data-host-label='prod-web-01']");
        await more.Locator("summary").ClickAsync();
        await more.Locator("[data-testid='advanced-sftp-host']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-sftp-page")).ToBeVisibleAsync();
        return page;
    }

    private static ILocator FolderActions(IPage page, string name) =>
        page.Locator($"[data-testid='advanced-folder-actions'][data-file-name='{name}']");

    private static ILocator Folder(IPage page, string name) =>
        page.Locator($"[data-testid='advanced-folder'][data-file-name='{name}']");

    [Fact]
    public async Task NormalFilesLandingKeepsAdvancedSftpContextual()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-files").ClickAsync();
        var more = page.Locator("[data-testid='files-host-more'][data-host-label='prod-web-01']");

        await Assertions.Expect(more).ToBeVisibleAsync();
        await Assertions.Expect(more.Locator("[data-testid='advanced-sftp-host']")).Not.ToBeVisibleAsync();
        await more.Locator("summary").ClickAsync();
        await Assertions.Expect(more.Locator("[data-testid='advanced-sftp-host']")).ToBeVisibleAsync();
    }

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

    [Fact]
    public async Task CurrentFolderCanBeBookmarkedReopenedAndDeleted()
    {
        var page = await OpenAdvancedAsync();

        await Folder(page, "releases").ClickAsync();
        await page.GetByTestId("advanced-save-bookmark").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-sftp-notice")).ToContainTextAsync("Saved releases bookmark");
        await page.GetByTestId("advanced-sftp-notice").GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();

        await page.GetByTestId("advanced-crumb").First.ClickAsync();
        await page.GetByTestId("advanced-show-bookmarks").ClickAsync();
        var bookmark = page.GetByTestId("advanced-bookmark");
        await Assertions.Expect(bookmark).ToContainTextAsync("releases");
        await Assertions.Expect(bookmark).ToContainTextAsync("/releases");

        await bookmark.GetByTestId("advanced-open-bookmark").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-crumbs")).ToContainTextAsync("releases");

        await page.GetByTestId("advanced-show-bookmarks").ClickAsync();
        await page.GetByTestId("advanced-delete-bookmark").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-bookmarks-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ProRemoteSearchFindsNestedFileAndCanOpenItsFolder()
    {
        var page = await OpenAdvancedAsync();

        await page.GetByTestId("sftp-search-query").FillAsync("settings");
        await page.GetByTestId("run-sftp-search").ClickAsync();

        await Assertions.Expect(page.GetByTestId("sftp-search-count")).ToContainTextAsync("1 result");
        var result = page.GetByTestId("sftp-search-result");
        await Assertions.Expect(result).ToContainTextAsync("settings.toml");
        await Assertions.Expect(result).ToContainTextAsync(".config/settings.toml");

        await result.GetByTestId("open-sftp-search-result").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-crumbs")).ToContainTextAsync(".config");
    }
}
