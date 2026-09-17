using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class HostDraftCancelTests(TestHostFixture fixture)
{
    [Fact]
    public async Task UntouchedNewHostCancelsWithoutPrompt()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        var prompted = false;
        page.Dialog += (_, _) => prompted = true;

        await page.GetByTestId("cancel-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        Assert.False(prompted);
    }

    [Fact]
    public async Task DirtyHostDraftCanKeepEditingWithoutLosingValues()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("unsaved-host");
        await page.GetByTestId("host-address").FillAsync("draft.example.com");

        var dialogSource = new TaskCompletionSource<IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Dialog += (_, dialog) => dialogSource.TrySetResult(dialog);

        var cancel = page.GetByTestId("cancel-host").ClickAsync();
        var dialog = await dialogSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("confirm", dialog.Type);
        Assert.Equal("Discard unsaved connection changes?", dialog.Message);
        await dialog.DismissAsync();
        await cancel;

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("unsaved-host");
        await Assertions.Expect(page.GetByTestId("host-address")).ToHaveValueAsync("draft.example.com");
    }

    [Fact]
    public async Task DirtyHostDraftRequiresExplicitDiscardBeforeLeaving()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("discard-me");

        var dialogSource = new TaskCompletionSource<IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Dialog += (_, dialog) => dialogSource.TrySetResult(dialog);

        var cancel = page.GetByTestId("cancel-host").ClickAsync();
        var dialog = await dialogSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dialog.AcceptAsync();
        await cancel;

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("discard-me", new() { Exact = true })).ToHaveCountAsync(0);
    }
}
