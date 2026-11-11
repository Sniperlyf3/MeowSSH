using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The Tailcat Transfers panel: list a peer's files and copy both ways, and
/// the public-relay question a pasted address gets every time.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class TailcatTransfersTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTransfersAsync(string query = "")
    {
        var page = await fixture.NewPageAsync(query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-section-transfers").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-list-files")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task ASavedPeerFillsTheAddressAndItsFilesAreListed()
    {
        var page = await OpenTransfersAsync();
        var list = page.GetByTestId("tailcat-list-files");
        await Assertions.Expect(list).ToBeDisabledAsync();

        await page.GetByTestId("tailcat-files-saved-peer").SelectOptionAsync(new SelectOptionValue { Label = "home-nas" });
        await Assertions.Expect(page.GetByTestId("tailcat-files-address")).ToHaveValueAsync("tcAbCdEf0123456789GhIjKlMnOpQrStUvWxYz");
        await page.GetByTestId("tailcat-files-path").FillAsync("/srv");
        await list.ClickAsync();

        var rows = page.GetByTestId("tailcat-file-row");
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(rows.First).ToContainTextAsync("Directory · Documents");
        await Assertions.Expect(rows.Last).ToContainTextAsync("File · hello.txt");
    }

    [Fact]
    public async Task FilesCopyBothWays()
    {
        var page = await OpenTransfersAsync();
        await page.GetByTestId("tailcat-files-address").FillAsync("tcAbCdEf0123456789GhIjKlMnOpQrStUvWxYz");
        var copy = page.GetByTestId("tailcat-copy");
        // Nothing to copy until there is a local path.
        await Assertions.Expect(copy).ToBeDisabledAsync();

        await page.GetByTestId("tailcat-copy-local").FillAsync("/sdcard/Download/report.pdf");
        await page.GetByTestId("tailcat-copy-remote").FillAsync("/inbox/report.pdf");
        await Assertions.Expect(copy).ToHaveTextAsync("Upload");
        await copy.ClickAsync();
        await Assertions.Expect(page.GetByText("Upload completed.")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Download", Exact = true }).First.ClickAsync();
        await Assertions.Expect(copy).ToHaveTextAsync("Download");
        await copy.ClickAsync();
        await Assertions.Expect(page.GetByText("Download completed.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task APastedPublicAddressIsAskedAboutEveryTimeWithNoWayToRememberIt()
    {
        var page = await OpenTransfersAsync("?multi&publicrelay");
        await page.GetByTestId("tailcat-files-address").FillAsync("tcAbCdEf0123456789GhIjKlMnOpQrStUvWxYz");

        await page.GetByTestId("tailcat-list-files").ClickAsync();
        await Assertions.Expect(page.GetByTestId("public-relay-prompt")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("public-relay-always")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("public-relay-save-hint")).ToBeVisibleAsync();
        await page.GetByTestId("public-relay-cancel").ClickAsync();
        await Assertions.Expect(page.GetByText("Did not list its files")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-file-row")).ToHaveCountAsync(0);

        await page.GetByTestId("tailcat-list-files").ClickAsync();
        await page.GetByTestId("public-relay-once").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-file-row")).ToHaveCountAsync(2);
    }
}
