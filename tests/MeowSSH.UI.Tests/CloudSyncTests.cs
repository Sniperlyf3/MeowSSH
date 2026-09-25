using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class CloudSyncTests(TestHostFixture fixture)
{
    private const string RecoveryCode = "000G-40R4-0M30-E209-185G-R38E-1W81-24GK";

    private async Task<IPage> OpenWithCloudBackupOnAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-backup").ClickAsync();
        await page.GetByTestId("cloud-enable-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-enable").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-sync-section")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task SyncIsOfferedOnlyOnceCloudBackupIsOn()
    {
        // Sync finds other phones through cloud backup's recovery-code
        // identity; offering it before that exists would be a dead switch.
        var page = await fixture.NewPageAsync("/?procloud");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-backup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-enable")).ToBeVisibleAsync();

        await Assertions.Expect(page.GetByTestId("cloud-sync-section")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TurningOnSyncSyncsStraightAwayAndSaysWhen()
    {
        var page = await OpenWithCloudBackupOnAsync("procloud");
        await Assertions.Expect(page.GetByTestId("cloud-sync-status")).ToHaveCountAsync(0);

        await page.GetByTestId("cloud-sync-toggle").CheckAsync();

        await Assertions.Expect(page.GetByTestId("cloud-sync-status")).ToContainTextAsync("Last synced");
        await Assertions.Expect(page.GetByTestId("cloud-sync-now")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task ASyncFailureIsShownInWordsAndSyncStaysOn()
    {
        var page = await OpenWithCloudBackupOnAsync("procloud&syncfails");

        await page.GetByTestId("cloud-sync-toggle").CheckAsync();

        await Assertions.Expect(page.GetByTestId("cloud-sync-error")).ToContainTextAsync("Try again");
        await Assertions.Expect(page.GetByTestId("cloud-sync-toggle")).ToBeCheckedAsync();
        await Assertions.Expect(page.GetByTestId("cloud-sync-status")).ToContainTextAsync("Not synced yet");
    }

    [Fact]
    public async Task TurningOffCloudBackupAlsoTurnsOffSync()
    {
        var page = await OpenWithCloudBackupOnAsync("procloud");
        await page.GetByTestId("cloud-sync-toggle").CheckAsync();
        await Assertions.Expect(page.GetByTestId("cloud-sync-status")).ToContainTextAsync("Last synced");

        await page.GetByTestId("cloud-disable").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-enable")).ToBeVisibleAsync();
        await page.GetByTestId("cloud-enable-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-enable").ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-sync-toggle")).Not.ToBeCheckedAsync();
    }

    [Fact]
    public async Task AFreeUserWhoRestoredFromTheCloudIsOfferedSyncAsAnUpgradeNotASwitch()
    {
        var page = await fixture.NewPageAsync("/?free&cloudseeded");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-backup").ClickAsync();
        await page.GetByTestId("cloud-restore-start").ClickAsync();
        await page.GetByTestId("cloud-restore-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-restore-find").ClickAsync();
        await page.GetByTestId("cloud-restore-confirm").CheckAsync();
        await page.GetByTestId("cloud-restore").ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-sync-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("cloud-sync-toggle")).ToHaveCountAsync(0);
    }
}
