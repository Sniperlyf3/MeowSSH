using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(UiCollection.Name)]
public sealed class SftpTests(UiFixture fixture)
{
    [Fact]
    public async Task FileCanBeRenamedFromActionsSheet()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl);
        await page.GetByTestId("host-row").First.ClickAsync();
        await page.GetByTestId("open-files").ClickAsync();

        await Assertions.Expect(page.Locator("[data-testid='file-entry'][data-file-name='start.sh']")).ToBeVisibleAsync();
        await page.Locator("[data-testid='file-actions'][data-file-name='start.sh']").ClickAsync();
        await page.GetByTestId("rename-file").ClickAsync();
        await page.GetByTestId("rename-name").FillAsync("renamed.sh");
        await page.GetByTestId("confirm-rename").ClickAsync();

        await Assertions.Expect(page.Locator("[data-testid='file-entry'][data-file-name='renamed.sh']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='file-entry'][data-file-name='start.sh']")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RenameRejectsPathTraversalNames()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl);
        await page.GetByTestId("host-row").First.ClickAsync();
        await page.GetByTestId("open-files").ClickAsync();

        await page.Locator("[data-testid='file-actions'][data-file-name='start.sh']").ClickAsync();
        await page.GetByTestId("rename-file").ClickAsync();
        await page.GetByTestId("rename-name").FillAsync("../escape.sh");
        await page.GetByTestId("confirm-rename").ClickAsync();

        await Assertions.Expect(page.GetByTestId("rename-error"))
            .ToContainTextAsync("without path separators");
        await Assertions.Expect(page.GetByTestId("rename-sheet")).ToBeVisibleAsync();
    }
}
