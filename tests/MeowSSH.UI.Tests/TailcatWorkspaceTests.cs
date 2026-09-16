using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class TailcatWorkspaceTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenWorkspacesAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await page.GetByTestId("tailcat-section-workspaces").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-workspaces")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task ProUserCanSaveStartAndStopWorkspace()
    {
        var page = await OpenWorkspacesAsync();

        await page.GetByTestId("new-tailcat-workspace").ClickAsync();
        await page.GetByTestId("tailcat-workspace-name").FillAsync("Home lab");
        await page.GetByTestId("tailcat-workspace-address").FillAsync("tc-home-lab");
        await page.GetByTestId("tailcat-workspace-key").FillAsync("client-default");
        await page.GetByTestId("add-tailcat-workspace-forward").ClickAsync();
        await page.GetByTestId("tailcat-workspace-forward-mappings").FillAsync("8080:80\n8443:443");
        await page.GetByTestId("save-tailcat-workspace").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-workspace-row")).ToContainTextAsync("Home lab");
        await Assertions.Expect(page.GetByTestId("tailcat-workspace-row")).ToContainTextAsync("1 forward group");

        await page.GetByTestId("start-tailcat-workspace").ClickAsync();
        await Assertions.Expect(page.GetByTestId("stop-tailcat-workspace")).ToBeVisibleAsync();

        await page.GetByTestId("tailcat-section-connect").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-socks-running")).ToContainTextAsync("127.0.0.1:19080");
        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToContainTextAsync("8080:80");

        await page.GetByTestId("tailcat-section-workspaces").ClickAsync();
        await page.GetByTestId("stop-tailcat-workspace").ClickAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-workspace")).ToBeVisibleAsync();

        await page.GetByTestId("tailcat-section-connect").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-socks-running")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WorkspaceCanBeEditedAndDeleted()
    {
        var page = await OpenWorkspacesAsync();

        await page.GetByTestId("new-tailcat-workspace").ClickAsync();
        await page.GetByTestId("tailcat-workspace-name").FillAsync("Temporary");
        await page.GetByTestId("save-tailcat-workspace").ClickAsync();

        await page.GetByTestId("edit-tailcat-workspace").ClickAsync();
        await page.GetByTestId("tailcat-workspace-name").FillAsync("Renamed");
        await page.GetByTestId("save-tailcat-workspace").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-workspace-row")).ToContainTextAsync("Renamed");

        await page.GetByTestId("delete-tailcat-workspace").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-workspaces-empty")).ToBeVisibleAsync();
    }
}
