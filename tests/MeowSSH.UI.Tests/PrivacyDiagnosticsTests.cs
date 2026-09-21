using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class PrivacyDiagnosticsTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenAsync(string query = "")
    {
        var page = await fixture.NewPageAsync("/" + query);
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
    public async Task SendDiagnosticsToggleIsOffByDefaultAndGatesTheResetButton()
    {
        var page = await OpenAsync();

        // Off by default, matching every other diagnostics default on this page --
        // and with it off, there is no id yet to reset, so the button stays hidden.
        await Assertions.Expect(page.GetByTestId("send-diagnostics-toggle")).Not.ToBeCheckedAsync();
        await Assertions.Expect(page.GetByTestId("reset-diagnostics-id")).Not.ToBeVisibleAsync();

        await page.GetByTestId("send-diagnostics-toggle").CheckAsync();
        await Assertions.Expect(page.GetByTestId("reset-diagnostics-id")).ToBeVisibleAsync();

        await page.GetByTestId("send-diagnostics-toggle").UncheckAsync();
        await Assertions.Expect(page.GetByTestId("reset-diagnostics-id")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task ResettingTheDiagnosticsIdentifierConfirmsInline()
    {
        var page = await OpenAsync();
        await page.GetByTestId("send-diagnostics-toggle").CheckAsync();

        await page.GetByTestId("reset-diagnostics-id").ClickAsync();

        await Assertions.Expect(page.GetByTestId("bug-report-message"))
            .ToContainTextAsync("Diagnostics identifier reset");
    }

    [Fact]
    public async Task SendButtonOnlyAppearsWithAPendingReportAndDiagnosticsAttached()
    {
        var page = await OpenAsync("?seed-pending-report");

        // A pending report exists, but "Attach anonymized crash diagnostics"
        // starts unchecked (matching every other diagnostics default on this
        // page), so there is nothing yet the send action would actually send.
        await Assertions.Expect(page.GetByTestId("attach-bug-diagnostics")).Not.ToBeCheckedAsync();
        await Assertions.Expect(page.GetByTestId("send-diagnostics")).ToHaveCountAsync(0);

        await page.GetByTestId("attach-bug-diagnostics").CheckAsync();

        await Assertions.Expect(page.GetByTestId("send-diagnostics")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SendingUploadsTheReportAndConfirmsInline()
    {
        var page = await OpenAsync("?seed-pending-report");
        await page.GetByTestId("attach-bug-diagnostics").CheckAsync();

        await page.GetByTestId("send-diagnostics").ClickAsync();

        await Assertions.Expect(page.GetByTestId("bug-report-message"))
            .ToContainTextAsync("Diagnostics sent");
    }

    [Fact]
    public async Task WithNoPendingReportThereIsNothingToSend()
    {
        var page = await OpenAsync();

        await Assertions.Expect(page.GetByTestId("no-pending-diagnostics")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("send-diagnostics")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task BackReturnsToCompactSettingsRoot()
    {
        var page = await OpenAsync();

        await page.GetByTestId("privacy-diagnostics-back").ClickAsync();

        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
    }
}
