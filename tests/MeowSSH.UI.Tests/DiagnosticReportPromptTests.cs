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

        await Assertions.Expect(page.GetByTestId("send-diagnostic-report")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("dismiss-diagnostic-report")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("discard-diagnostic-report")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diagnostic-exception-summary")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task DetailsAreOneTapAwayAndExplainWhatIsAndIsNotShared()
    {
        var page = await OpenPromptAsync();

        await page.GetByText("Review what’s included", new() { Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-exception-summary")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diagnostic-report-prompt"))
            .ToContainTextAsync("Not included: terminal contents, commands, host names, addresses, usernames, file names, passwords, keys, or account identifiers.");
        await Assertions.Expect(page.GetByTestId("discard-diagnostic-report")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PrimarySendPathTakesOneTap()
    {
        var page = await OpenPromptAsync();

        await page.GetByTestId("send-diagnostic-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-result")).ToContainTextAsync("Send requested");
    }

    [Fact]
    public async Task NotNowTakesOneTapAndDoesNotRequireReviewingDetails()
    {
        var page = await OpenPromptAsync();

        await page.GetByTestId("dismiss-diagnostic-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-result")).ToContainTextAsync("Deferred");
    }

    [Fact]
    public async Task DestructiveDiscardIsAvailableOnlyInsideTheReviewDisclosure()
    {
        var page = await OpenPromptAsync();
        await page.GetByText("Review what’s included", new() { Exact = true }).ClickAsync();

        await page.GetByTestId("discard-diagnostic-report").ClickAsync();

        await Assertions.Expect(page.GetByTestId("diagnostic-result")).ToContainTextAsync("Discarded");
    }

    [Fact]
    public async Task PromptDoesNotCreateHorizontalOverflowAtPhoneWidth()
    {
        var page = await OpenPromptAsync();
        await page.GetByText("Review what’s included", new() { Exact = true }).ClickAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");

        Assert.False(overflows, "The diagnostic consent prompt scrolls horizontally at phone width.");
    }
}
