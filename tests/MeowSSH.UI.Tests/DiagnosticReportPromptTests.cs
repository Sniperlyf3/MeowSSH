using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class DiagnosticReportPromptTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenPromptAsync()
    {
        var page = await fixture.NewPageAsync("/?diagnostic");
        await Assertions.Expect(page.GetByTestId("diagnostic-report-prompt")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task KeepsTheDefaultDecisionSurfaceToTwoVisibleActions()
    {
        var page = await OpenPromptAsync();

        await Assertions.Expect(page.GetByTestId("save-diagnostic-report")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("discard-diagnostic-report")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diagnostic-exception-summary")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diagnostic-report-prompt"))
            .ToContainTextAsync("Nothing was uploaded");
    }

    [Fact]
    public async Task DetailsAreOneTapAwayAndExplainWhatIsAndIsNotShared()
    {
        var page = await OpenPromptAsync();

        await page.GetByText("Review saved report", new() { Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-exception-summary")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diagnostic-report-prompt"))
            .ToContainTextAsync("Not included: terminal contents, commands, host names, addresses, usernames, file names, passwords, keys, or account identifiers.");
    }

    [Fact]
    public async Task PrimarySavePathTakesOneTap()
    {
        var page = await OpenPromptAsync();

        await page.GetByTestId("save-diagnostic-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-result")).ToContainTextAsync("Save requested");
    }

    [Fact]
    public async Task DiscardTakesOneTapWithoutRequiringReview()
    {
        var page = await OpenPromptAsync();

        await page.GetByTestId("discard-diagnostic-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-result")).ToContainTextAsync("Discarded");
    }

    [Fact]
    public async Task PromptDoesNotCreateHorizontalOverflowAtPhoneWidth()
    {
        var page = await OpenPromptAsync();
        await page.GetByText("Review saved report", new() { Exact = true }).ClickAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "The diagnostic consent prompt scrolls horizontally at phone width.");
    }
}
