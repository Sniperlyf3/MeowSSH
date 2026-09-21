using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class FileBrowserTests(TestHostFixture fixture)
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
    public async Task OpensInTheSessionsOwnDirectoryRatherThanTheRoot()
    {
        var page = await OpenFilesAsync();
        await Assertions.Expect(page.GetByTestId("crumbs")).ToContainTextAsync("deploy");
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DirectoriesSortAboveFiles()
    {
        var page = await OpenFilesAsync();
        var names = await page.Locator("[data-testid=file-entry]").EvaluateAllAsync<string[]>(
            "els => els.map(e => e.dataset.fileName)");
        Assert.Equal("releases", names[0]);
        Assert.Contains("start.sh", names);
    }

    [Fact]
    public async Task HiddenFilesAreOutOfTheWayUntilAskedFor()
    {
        var page = await OpenFilesAsync();
        await Assertions.Expect(Entry(page, ".bashrc")).ToHaveCountAsync(0);
        await page.GetByTestId("toggle-hidden").ClickAsync();
        await Assertions.Expect(Entry(page, ".bashrc")).ToBeVisibleAsync();
        await Assertions.Expect(Entry(page, ".config")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TappingADirectoryNavigatesIntoIt()
    {
        var page = await OpenFilesAsync();
        await Entry(page, "releases").ClickAsync();
        await Assertions.Expect(Entry(page, "2026-09-11")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("crumbs")).ToContainTextAsync("releases");
    }

    [Fact]
    public async Task ABreadcrumbNavigatesBackUp()
    {
        var page = await OpenFilesAsync();
        await Entry(page, "releases").ClickAsync();
        await Assertions.Expect(Entry(page, "2026-09-11")).ToBeVisibleAsync();
        await page.GetByTestId("crumb").Filter(new() { HasTextString = "deploy" }).ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FileSizesAndPermissionsAreShown()
    {
        var page = await OpenFilesAsync();
        var row = Entry(page, "start.sh");
        await Assertions.Expect(row).ToContainTextAsync("rwxr-xr-x");
        await Assertions.Expect(row).ToContainTextAsync("402 B");
        await Assertions.Expect(Entry(page, "app.log")).ToContainTextAsync("18.4 MB");
        await Assertions.Expect(Entry(page, "backup-2026-09.tar.gz")).ToContainTextAsync("1.2 GB");
    }

    [Fact]
    public async Task ADirectoryShowsNoSize()
    {
        var page = await OpenFilesAsync();
        await Assertions.Expect(Entry(page, "releases")).Not.ToContainTextAsync("B");
    }

    [Fact]
    public async Task AnEmptyDirectorySaysSoRatherThanShowingNothing()
    {
        var page = await OpenFilesAsync();
        await Entry(page, "releases").ClickAsync();
        await Entry(page, "2026-09-11").ClickAsync();
        await Assertions.Expect(page.GetByTestId("files-empty")).ToContainTextAsync("empty");
    }

    [Fact]
    public async Task CreatingAFolderShowsItInTheListing()
    {
        var page = await OpenFilesAsync();
        await page.GetByTestId("new-folder").ClickAsync();
        await page.GetByTestId("new-folder-name").FillAsync("new-release");
        await page.GetByTestId("create-folder").ClickAsync();
        await Assertions.Expect(Entry(page, "new-release")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FileCanBeRenamedFromActionsSheet()
    {
        var page = await OpenFilesAsync();
        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("rename-file").ClickAsync();
        await page.GetByTestId("rename-name").FillAsync("renamed.sh");
        await page.GetByTestId("confirm-rename").ClickAsync();
        await Assertions.Expect(Entry(page, "renamed.sh")).ToBeVisibleAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RenameRejectsPathTraversalNames()
    {
        var page = await OpenFilesAsync();
        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("rename-file").ClickAsync();
        await page.GetByTestId("rename-name").FillAsync("../escape.sh");
        await page.GetByTestId("confirm-rename").ClickAsync();
        await Assertions.Expect(page.GetByTestId("rename-error"))
            .ToContainTextAsync("without path separators");
        await Assertions.Expect(page.GetByTestId("rename-sheet")).ToBeVisibleAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task FileCanBeMovedToAnotherDirectory()
    {
        var page = await OpenFilesAsync();
        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/home/deploy/releases");
        await page.GetByTestId("confirm-move").ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToHaveCountAsync(0);
        await Entry(page, "releases").ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DirectoryMovePreservesItsSubtree()
    {
        var page = await OpenFilesAsync();
        await Actions(page, "releases").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/var");
        await page.GetByTestId("confirm-move").ClickAsync();
        await Assertions.Expect(Entry(page, "releases")).ToHaveCountAsync(0);
        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "var").ClickAsync();
        await Entry(page, "releases").ClickAsync();
        await Assertions.Expect(Entry(page, "2026-09-11")).ToBeVisibleAsync();
        await Assertions.Expect(Entry(page, "2026-09-04")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DirectoryCannotBeMovedIntoItsOwnDescendant()
    {
        var page = await OpenFilesAsync();
        await Actions(page, "releases").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/home/deploy/releases/2026-09-11");
        await page.GetByTestId("confirm-move").ClickAsync();
        await Assertions.Expect(page.GetByTestId("move-error"))
            .ToContainTextAsync("cannot be moved into itself");
        await Assertions.Expect(page.GetByTestId("move-sheet")).ToBeVisibleAsync();
    }

    private static async Task ReturnToHomeAsync(IPage page)
    {
        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "home").ClickAsync();
        await Entry(page, "deploy").ClickAsync();
    }

    [Fact]
    public async Task MovingOntoAnExistingNameShowsAConflictPromptRatherThanFailingSilently()
    {
        var page = await OpenFilesAsync();

        // Give /var its own start.sh -- no conflict there yet, so this is a plain upload.
        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "var").ClickAsync();
        await page.GetByTestId("upload-file").ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
        await ReturnToHomeAsync(page);

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/var");
        await page.GetByTestId("confirm-move").ClickAsync();

        await Assertions.Expect(page.GetByTestId("move-conflict")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("move-conflict")).ToContainTextAsync("Replace start.sh?");
        await Assertions.Expect(page.GetByTestId("move-sheet")).ToHaveCountAsync(0);

        await page.GetByTestId("cancel-move-conflict").ClickAsync();
        await Assertions.Expect(page.GetByTestId("move-conflict")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("move-sheet")).ToBeVisibleAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ConfirmingAMoveConflictReplacesTheDestinationFileRatherThanDuplicatingIt()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "var").ClickAsync();
        await page.GetByTestId("upload-file").ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToBeVisibleAsync();
        await ReturnToHomeAsync(page);

        await Actions(page, "start.sh").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/var");
        await page.GetByTestId("confirm-move").ClickAsync();
        await Assertions.Expect(page.GetByTestId("move-conflict")).ToBeVisibleAsync();

        await page.GetByTestId("replace-move").ClickAsync();

        await Assertions.Expect(page.GetByTestId("move-conflict")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("move-sheet")).ToHaveCountAsync(0);
        await Assertions.Expect(Entry(page, "start.sh")).ToHaveCountAsync(0);

        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "var").ClickAsync();
        await Assertions.Expect(Entry(page, "start.sh")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task MoveRejectsADirectoryNameCollisionWithoutOfferingToReplaceIt()
    {
        var page = await OpenFilesAsync();

        await page.GetByTestId("new-folder").ClickAsync();
        await page.GetByTestId("new-folder-name").FillAsync("shared-name");
        await page.GetByTestId("create-folder").ClickAsync();
        await Assertions.Expect(Entry(page, "shared-name")).ToBeVisibleAsync();

        await page.GetByTestId("crumb").First.ClickAsync();
        await Entry(page, "var").ClickAsync();
        await page.GetByTestId("new-folder").ClickAsync();
        await page.GetByTestId("new-folder-name").FillAsync("shared-name");
        await page.GetByTestId("create-folder").ClickAsync();
        await Assertions.Expect(Entry(page, "shared-name")).ToBeVisibleAsync();
        await ReturnToHomeAsync(page);

        await Actions(page, "shared-name").ClickAsync();
        await page.GetByTestId("move-file").ClickAsync();
        await page.GetByTestId("move-destination").FillAsync("/var");
        await page.GetByTestId("confirm-move").ClickAsync();

        await Assertions.Expect(page.GetByTestId("move-error"))
            .ToContainTextAsync("already exists in that destination");
        await Assertions.Expect(page.GetByTestId("move-sheet")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("move-conflict")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheFileBrowserDoesNotScrollSideways()
    {
        var page = await OpenFilesAsync();
        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflows, "The file browser scrolls horizontally at 390px wide.");
    }

    [Fact]
    public async Task ClosingTheBrowserReturnsToTheHostList()
    {
        var page = await OpenFilesAsync();
        await page.GetByTestId("files-back").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
    }
}
