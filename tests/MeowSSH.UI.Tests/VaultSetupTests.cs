using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class VaultSetupTests(TestHostFixture fixture)
{
    [Fact]
    public async Task AFirstRunOffersSetupRatherThanAnUnlockScreen()
    {
        var page = await fixture.NewPageAsync("/?setup");

        // Offering "unlock with biometrics" before a vault exists is a button
        // that cannot work, on the very first screen anyone sees.
        await Assertions.Expect(page.GetByTestId("setup-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("lock-screen")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CreatingTheVaultShowsTheRecoveryCodeOnce()
    {
        var page = await fixture.NewPageAsync("/?setup");

        await page.GetByTestId("create-vault").ClickAsync();

        var code = page.GetByTestId("recovery-code");
        await Assertions.Expect(code).ToBeVisibleAsync();
        // A blank panel here would be worse than no panel: the user would tap
        // past it believing they had seen everything there was to see.
        await Assertions.Expect(code).ToContainTextAsync(new System.Text.RegularExpressions.Regex("[0-9A-Z]{4}"));
    }

    [Fact]
    public async Task TheUserCannotLeaveTheCodeScreenWithoutConfirmingTheyWroteItDown()
    {
        var page = await fixture.NewPageAsync("/?setup");
        await page.GetByTestId("create-vault").ClickAsync();

        var finish = page.GetByTestId("finish-setup");
        await Assertions.Expect(finish).ToBeDisabledAsync();

        await page.GetByTestId("confirm-written-down").Locator("input").CheckAsync();

        await Assertions.Expect(finish).ToBeEnabledAsync();
    }

    [Fact]
    public async Task FinishingSetupLandsOnTheHostList()
    {
        var page = await fixture.NewPageAsync("/?setup");
        await page.GetByTestId("create-vault").ClickAsync();
        await page.GetByTestId("confirm-written-down").Locator("input").CheckAsync();

        await page.GetByTestId("finish-setup").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        // The code is dropped rather than kept around once the vault is open.
        await Assertions.Expect(page.GetByTestId("recovery-code")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheCodeIsSelectableSoItCanBeCopiedWithoutTheClipboardApi()
    {
        var page = await fixture.NewPageAsync("/?setup");
        await page.GetByTestId("create-vault").ClickAsync();

        // An embedded web view can refuse navigator.clipboard outright. A code
        // that can be neither copied nor selected is a code that gets mistyped.
        var userSelect = await page.GetByTestId("recovery-code").Locator("code")
            .EvaluateAsync<string>("el => getComputedStyle(el).userSelect || getComputedStyle(el).webkitUserSelect");

        Assert.Equal("all", userSelect);
    }
}
