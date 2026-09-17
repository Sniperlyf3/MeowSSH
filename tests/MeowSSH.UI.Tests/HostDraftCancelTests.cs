using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class HostDraftCancelTests(TestHostFixture fixture)
{
    [Fact]
    public async Task UntouchedNewHostCancelsWithoutPrompt()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("cancel-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("discard-host-draft")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DirtyHostDraftCanKeepEditingWithoutLosingValues()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("unsaved-host");
        await page.GetByTestId("host-address").FillAsync("draft.example.com");

        await page.GetByTestId("cancel-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("discard-host-draft")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await page.GetByTestId("keep-host-draft").ClickAsync();

        await Assertions.Expect(page.GetByTestId("discard-host-draft")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("unsaved-host");
        await Assertions.Expect(page.GetByTestId("host-address")).ToHaveValueAsync("draft.example.com");
    }

    [Fact]
    public async Task DirtyHostDraftRequiresExplicitDiscardBeforeLeaving()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("discard-me");

        await page.GetByTestId("cancel-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("discard-host-draft")).ToBeVisibleAsync();
        await page.GetByTestId("discard-host-draft-confirm").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("discard-me", new() { Exact = true })).ToHaveCountAsync(0);
    }
}
