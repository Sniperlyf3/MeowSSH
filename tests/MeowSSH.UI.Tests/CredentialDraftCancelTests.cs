using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class CredentialDraftCancelTests(TestHostFixture fixture)
{
    [Fact]
    public async Task UntouchedCredentialPickerCancelsWithoutPrompt()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();

        await page.GetByTestId("cancel-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("discard-credential-draft")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DirtyPasswordDraftCanKeepEditingWithoutLosingValues()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-password").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("staging password");
        await page.GetByTestId("credential-secret").FillAsync("draft-secret");

        await page.GetByTestId("cancel-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("discard-credential-draft")).ToBeVisibleAsync();
        await page.GetByTestId("keep-credential-draft").ClickAsync();

        await Assertions.Expect(page.GetByTestId("discard-credential-draft")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("credential-label")).ToHaveValueAsync("staging password");
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveValueAsync("draft-secret");
    }

    [Fact]
    public async Task DirtyCredentialDraftRequiresExplicitDiscardBeforeClosing()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-public-key").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("temporary key");
        await page.GetByTestId("credential-public-key").FillAsync("ssh-ed25519 AAAATemporary");

        await page.GetByTestId("cancel-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-editor")).ToBeVisibleAsync();
        await page.GetByTestId("discard-credential-draft-confirm").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ChangingTypeProtectsOnlyMaterialThatWouldBeLost()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-import-key").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("shared label");
        await page.GetByTestId("credential-secret").FillAsync("private material");

        await page.GetByTestId("change-credential-type").ClickAsync();
        await Assertions.Expect(page.GetByTestId("discard-credential-draft")).ToBeVisibleAsync();
        await page.GetByTestId("keep-credential-draft").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveValueAsync("private material");

        await page.GetByTestId("change-credential-type").ClickAsync();
        await page.GetByTestId("discard-credential-draft-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToBeVisibleAsync();

        await page.GetByTestId("flow-password").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-label")).ToHaveValueAsync("shared label");
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveValueAsync("");
    }
}
