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

    [Fact]
    public async Task OpensInTheSessionsOwnDirectoryRatherThanTheRoot()
    {
        // SFTP has no home-directory request, so the browser resolves "." to
        // find where the session opened. Landing at / would make every user
        // navigate down to their own files on every visit.
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

        // "releases" sorts after "app.log" and "backup..." alphabetically, so
        // finding it first proves directories are grouped, not just sorted.
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
        // 0755 rendered the way ls -l prints it, so a user can tell at a glance
        // whether a script is executable.
        await Assertions.Expect(row).ToContainTextAsync("rwxr-xr-x");
        await Assertions.Expect(row).ToContainTextAsync("402 B");

        // Decimal units, matching how storage is labelled everywhere else.
        await Assertions.Expect(Entry(page, "app.log")).ToContainTextAsync("18.4 MB");
        await Assertions.Expect(Entry(page, "backup-2026-09.tar.gz")).ToContainTextAsync("1.2 GB");
    }

    [Fact]
    public async Task ADirectoryShowsNoSize()
    {
        // A byte count for a directory is noise: it is the size of the directory
        // entry, not of what is inside it, and users read it as the latter.
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
