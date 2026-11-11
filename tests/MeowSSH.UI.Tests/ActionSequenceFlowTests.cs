using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The parts of multi-step and parameterized Actions a user reaches past the
/// happy path ActionsTests covers: runtime values for a sequence, stopping at
/// a failed step, editing and removing steps, and a value left empty.
/// </summary>
/// <remarks>FakeSshEngine fails any command containing "fail" with a non-zero exit.</remarks>
[Collection(nameof(TestHostCollection))]
public sealed class ActionSequenceFlowTests(TestHostFixture fixture)
{
    private async Task<(IPage Page, ILocator Card)> CreateActionAsync(string name, string command)
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();
        await page.GetByTestId("add-action").ClickAsync();
        await page.GetByTestId("action-name").FillAsync(name);
        await page.GetByTestId("action-command").FillAsync(command);
        await page.Locator("label.action-host").Filter(new() { HasTextString = "prod-web-01" }).Locator("input[type='checkbox']").CheckAsync();
        await page.GetByTestId("save-action").ClickAsync();
        var card = page.Locator("[data-testid^='action-card-']").Filter(new() { HasTextString = name });
        await Assertions.Expect(card).ToBeVisibleAsync();
        return (page, card);
    }

    private static async Task ConfigureStepsAsync(IPage page, ILocator card, string steps, bool stopOnError)
    {
        await card.Locator("details.action-card__manage > summary").ClickAsync();
        await card.GetByTestId("configure-action-sequence").ClickAsync();
        await page.GetByTestId("action-sequence-commands").FillAsync(steps);
        var stop = page.GetByTestId("action-sequence-stop-on-error");
        if (stopOnError) await stop.CheckAsync(); else await stop.UncheckAsync();
        await page.GetByTestId("save-action-sequence").ClickAsync();
        await Assertions.Expect(page.GetByTestId("action-sequence-dialog")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ASequenceAsksForItsValuesOnceAndStopsAtTheFirstFailedStep()
    {
        var (page, card) = await CreateActionAsync("Release", "echo {{version}}");
        await ConfigureStepsAsync(page, card, "echo fail-migrate\necho announce {{version}}", stopOnError: true);
        await Assertions.Expect(card.GetByTestId("action-sequence-summary")).ToHaveTextAsync("3 steps · stop on error");

        await card.GetByTestId("run-action-sequence").ClickAsync();
        var values = page.GetByTestId("action-sequence-values");
        await Assertions.Expect(values).ToContainTextAsync("not saved");
        // One value for every step that uses it.
        await Assertions.Expect(values.Locator("[data-testid^='sequence-variable-']")).ToHaveCountAsync(1);
        await page.GetByTestId("sequence-variable-version").FillAsync("2.4.1");
        await page.GetByTestId("confirm-action-sequence").ClickAsync();

        var results = card.GetByTestId("action-sequence-results");
        await Assertions.Expect(results).ToContainTextAsync("Sequence completed with failures");
        var host = results.GetByTestId("action-sequence-host-result");
        await Assertions.Expect(host).ToContainTextAsync("prod-web-01");
        await Assertions.Expect(host).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-failure"));
        // Step 3 never ran.
        await Assertions.Expect(results.GetByTestId("action-sequence-step-result")).ToHaveCountAsync(2);
        await Assertions.Expect(results).Not.ToContainTextAsync("announce");
    }

    [Fact]
    public async Task WithStopOnErrorOffEveryStepRuns()
    {
        var (page, card) = await CreateActionAsync("Best effort", "echo first");
        await ConfigureStepsAsync(page, card, "echo fail-middle\necho last", stopOnError: false);
        await Assertions.Expect(card.GetByTestId("action-sequence-summary")).ToHaveTextAsync("3 steps · continue on error");

        await card.GetByTestId("run-action-sequence").ClickAsync();
        var results = card.GetByTestId("action-sequence-results");
        await Assertions.Expect(results.GetByTestId("action-sequence-step-result")).ToHaveCountAsync(3);
        await Assertions.Expect(results).ToContainTextAsync("echo last on");
    }

    [Fact]
    public async Task StepsCanBeEditedAndRemoved()
    {
        var (page, card) = await CreateActionAsync("Editable", "echo one");
        await ConfigureStepsAsync(page, card, "echo two", stopOnError: true);
        await Assertions.Expect(card.GetByTestId("action-sequence-summary")).ToContainTextAsync("2 steps");

        await card.GetByTestId("edit-action-sequence").ClickAsync();
        await Assertions.Expect(page.GetByTestId("action-sequence-commands")).ToHaveValueAsync("echo two");
        await page.GetByTestId("action-sequence-commands").FillAsync("echo two\necho three\necho four");
        await page.GetByTestId("save-action-sequence").ClickAsync();
        await Assertions.Expect(card.GetByTestId("action-sequence-summary")).ToContainTextAsync("4 steps");

        await card.GetByTestId("edit-action-sequence").ClickAsync();
        await page.GetByTestId("delete-action-sequence").ClickAsync();
        await Assertions.Expect(card.GetByTestId("action-sequence-summary")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("configure-action-sequence")).ToBeAttachedAsync();
    }

    [Fact]
    public async Task AnEmptyValueIsNamedAndNothingRuns()
    {
        var (page, card) = await CreateActionAsync("Needs a value", "echo {{target}}");
        await card.Locator("[data-testid^='run-parameterized-action-']").ClickAsync();
        await Assertions.Expect(page.GetByTestId("action-values-dialog")).ToContainTextAsync("Needs a value");
        await page.GetByTestId("confirm-parameterized-action").ClickAsync();

        await Assertions.Expect(page.GetByTestId("action-variable-message")).ToContainTextAsync("Enter a value for Action variable 'target'");
        await Assertions.Expect(card.GetByTestId("parameterized-action-results")).ToHaveCountAsync(0);
    }
}
