using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class PrivacyDiagnosticsTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-privacy-diagnostics").ClickAsync();
        await Assertions.Expect(page.GetByTestId("privacy-diagnostics-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task BugReportStartsWithOnlyTheRequiredDescription()
    {
        var page = await OpenAsync();

        await Assertions.Expect(page.GetByTestId("bug-description")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("bug-expected")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("bug-steps")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("export-bug-report")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("attach-bug-diagnostics")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("privacy-diagnostics-page"))
            .ToContainTextAsync("Nothing is uploaded automatically");
    }

    [Fact]
    public async Task OptionalReproductionFieldsAreOneDisclosureAway()
    {
        var page = await OpenAsync();

        await page.GetByTestId("bug-report-more-details").Locator("summary").ClickAsync();

        await Assertions.Expect(page.GetByTestId("bug-expected")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("bug-steps")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task UserCanExportAReportWithoutDiagnostics()
    {
        var page = await OpenAsync();
        await page.GetByTestId("bug-description").FillAsync("Files did not open after I switched views.");

        await page.GetByTestId("export-bug-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("bug-report-message"))
            .ToContainTextAsync("Bug report exported to");
    }

    [Fact]
    public async Task BackReturnsToCompactSettingsRoot()
    {
        var page = await OpenAsync();

        await page.GetByTestId("privacy-diagnostics-back").ClickAsync();

        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
    }
}
