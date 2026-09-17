using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class ActionDraftCancelTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenActionsAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();
        await Assertions.Expect(page.GetByTestId("actions-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task DirtyActionDraftRequiresExplicitDiscardAndCanResumeEditing()
    {
        var page = await OpenActionsAsync();
        await page.GetByTestId("add-action").ClickAsync();
        await page.GetByTestId("action-name").FillAsync("Unsaved deployment");
        await page.GetByTestId("action-command").FillAsync("echo pending");

        await page.GetByTestId("cancel-action").ClickAsync();

        await Assertions.Expect(page.GetByTestId("discard-action-draft")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("action-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("action-name")).ToHaveValueAsync("Unsaved deployment");

        await page.GetByTestId("keep-action-draft").ClickAsync();

        await Assertions.Expect(page.GetByTestId("discard-action-draft")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("action-name")).ToHaveValueAsync("Unsaved deployment");

        await page.GetByTestId("cancel-action").ClickAsync();
        await page.GetByTestId("discard-action-draft-confirm").ClickAsync();

        await Assertions.Expect(page.GetByTestId("action-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("Unsaved deployment", new() { Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task UntouchedActionDraftCancelsImmediately()
    {
        var page = await OpenActionsAsync();
        await page.GetByTestId("add-action").ClickAsync();

        await page.GetByTestId("cancel-action").ClickAsync();

        await Assertions.Expect(page.GetByTestId("action-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("discard-action-draft")).ToHaveCountAsync(0);
    }
}
