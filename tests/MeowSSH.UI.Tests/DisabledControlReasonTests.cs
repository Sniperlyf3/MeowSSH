using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Regression coverage for audit items A3/A4: a dimmed primary action with no
/// stated reason is a dead end -- the user can see the control they want and
/// has no way to learn what unlocks it.
/// </summary>
/// <remarks>
/// One pattern is used everywhere: while the button is disabled, a
/// <c>form__hint</c> naming the single unmet condition sits directly above it,
/// and the hint disappears (or names the next condition) once that condition
/// is met. These tests assert the hint is actually there on all three screens
/// the audit named, not just that the button is disabled -- a bare
/// <c>ToBeDisabledAsync()</c> would still pass on the pre-fix code that had no
/// explanation at all.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class DisabledControlReasonTests(TestHostFixture fixture)
{
    [Fact]
    public async Task DisabledSaveConnectionNamesTheMissingField()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        var save = page.GetByTestId("save-host");
        var hint = page.GetByTestId("save-host-hint");

        await Assertions.Expect(save).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(save).ToHaveAttributeAsync("aria-describedby", "save-host-hint");

        await page.GetByTestId("host-label").FillAsync("build-01");
        await page.GetByTestId("host-address").FillAsync("build.example.com");

        await Assertions.Expect(save).ToBeEnabledAsync();
        await Assertions.Expect(hint).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DisabledStartSharingNamesTheMissingClientAllowlist()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();

        var start = page.GetByTestId("start-tailcat-server");
        var hint = page.GetByTestId("start-tailcat-server-hint");

        await Assertions.Expect(start).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(hint).ToContainTextAsync("say who may connect");

        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await Assertions.Expect(start).ToBeDisabledAsync();
        // Naming a client is not the only condition: no service is turned on
        // yet, so the hint should now name that instead of vanishing outright.
        await Assertions.Expect(hint).ToContainTextAsync("Turn on at least one service");

        await page.GetByTestId("tailcat-exit-node").CheckAsync();
        await Assertions.Expect(start).ToBeEnabledAsync();
        await Assertions.Expect(hint).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DisabledExportReportNamesTheMissingDescription()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-privacy-diagnostics").ClickAsync();
        await Assertions.Expect(page.GetByTestId("privacy-diagnostics-page")).ToBeVisibleAsync();

        var export = page.GetByTestId("export-bug-report");
        var hint = page.GetByTestId("export-bug-report-hint");

        await Assertions.Expect(export).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(hint).ToContainTextAsync("Describe what went wrong");

        await page.GetByTestId("bug-description").FillAsync("Files did not open after I switched views.");

        await Assertions.Expect(export).ToBeEnabledAsync();
        await Assertions.Expect(hint).ToHaveCountAsync(0);
    }
}
