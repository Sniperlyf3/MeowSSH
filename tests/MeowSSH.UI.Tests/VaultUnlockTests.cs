using MeowSSH.TestHost;
using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class VaultUnlockTests(TestHostFixture fixture)
{
    [Fact]
    public async Task LockedVaultShowsTheUnlockScreenAndNoHosts()
    {
        var page = await fixture.NewPageAsync("/?locked");

        await Assertions.Expect(page.GetByTestId("lock-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-row")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task BiometricUnlockRevealsTheHostList()
    {
        var page = await fixture.NewPageAsync("/?locked");

        await page.GetByTestId("unlock-biometric").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task RecoveryUnlockIsRefusedUntilTheCodeIsComplete()
    {
        var page = await fixture.NewPageAsync("/?locked");
        await page.GetByTestId("use-recovery").ClickAsync();

        var submit = page.GetByTestId("submit-recovery");
        await Assertions.Expect(submit).ToBeDisabledAsync();

        await page.GetByTestId("recovery-input").FillAsync("ABCD-EFGH");
        await Assertions.Expect(submit).ToBeDisabledAsync();

        await page.GetByTestId("recovery-input").FillAsync(TestHostDefaults.RecoveryCode);
        await Assertions.Expect(submit).ToBeEnabledAsync();
    }

    [Fact]
    public async Task CorrectRecoveryCodeUnlocksTheVault()
    {
        var page = await fixture.NewPageAsync("/?locked");
        await page.GetByTestId("use-recovery").ClickAsync();
        await page.GetByTestId("recovery-input").FillAsync(TestHostDefaults.RecoveryCode);

        await page.GetByTestId("submit-recovery").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WrongRecoveryCodeExplainsItselfWithoutUnlocking()
    {
        var page = await fixture.NewPageAsync("/?locked");
        await page.GetByTestId("use-recovery").ClickAsync();
        await page.GetByTestId("recovery-input").FillAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ");

        await page.GetByTestId("submit-recovery").ClickAsync();

        await Assertions.Expect(page.GetByTestId("unlock-error")).ToContainTextAsync("does not match");
        await Assertions.Expect(page.GetByTestId("lock-screen")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ARecoveryCodeTypedTheWayPeopleWriteItStillWorks()
    {
        var page = await fixture.NewPageAsync("/?locked");
        await page.GetByTestId("use-recovery").ClickAsync();

        // Lower case, spaces instead of dashes: how a code comes off a sticky note.
        await page.GetByTestId("recovery-input")
            .FillAsync(TestHostDefaults.RecoveryCode.Replace('-', ' ').ToLowerInvariant());
        await page.GetByTestId("submit-recovery").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
    }
}
