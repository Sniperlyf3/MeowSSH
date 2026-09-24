using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class CloudBackupTests(TestHostFixture fixture)
{
    private const string RecoveryCode = "000G-40R4-0M30-E209-185G-R38E-1W81-24GK";

    private async Task<IPage> OpenAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-backup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-backup-section")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task TurningOnCloudBackupBacksUpImmediatelyAndListsTheVersion()
    {
        var page = await OpenAsync("procloud");

        await page.GetByTestId("cloud-enable-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-enable").ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-backup-message")).ToContainTextAsync("backed up just now");
        await Assertions.Expect(page.GetByTestId("cloud-version")).ToHaveCountAsync(1);

        await page.GetByTestId("cloud-backup-now").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-version")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task AnIncompleteRecoveryCodeIsRefusedAndNothingIsTurnedOn()
    {
        var page = await OpenAsync("procloud");

        await page.GetByTestId("cloud-enable-code").FillAsync("000G-40R4");
        await page.GetByTestId("cloud-enable").ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-backup-message")).ToContainTextAsync("not complete");
        await Assertions.Expect(page.GetByTestId("cloud-backup-now")).ToHaveCountAsync(0);
        // The field is cleared rather than left holding a half-typed secret.
        await Assertions.Expect(page.GetByTestId("cloud-enable-code")).ToHaveValueAsync("");
    }

    [Fact]
    public async Task ProLocalSeesTheUpsellInsteadOfTheUploadControls()
    {
        var page = await OpenAsync("tab-tools");

        await Assertions.Expect(page.GetByTestId("cloud-backup-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("cloud-enable")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("cloud-restore-start")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AFreeUserCanStillRestoreACloudBackup()
    {
        // A lapsed Pro Cloud subscriber is Free: getting their own vault back
        // must not be behind the paywall that stopped their uploads.
        var page = await OpenAsync("free&cloudseeded");

        await page.GetByTestId("cloud-restore-start").ClickAsync();
        await page.GetByTestId("cloud-restore-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-restore-find").ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-restore-version")).ToHaveCountAsync(2);
        var restore = page.GetByTestId("cloud-restore");
        await Assertions.Expect(restore).ToBeDisabledAsync();

        await page.GetByTestId("cloud-restore-confirm").CheckAsync();
        await restore.ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-backup-message")).ToContainTextAsync("Cloud backup restored");
        await Assertions.Expect(page.GetByTestId("cloud-rebind")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DeletingCloudBackupsNeedsAnExplicitConfirmation()
    {
        var page = await OpenAsync("procloud");
        await page.GetByTestId("cloud-enable-code").FillAsync(RecoveryCode);
        await page.GetByTestId("cloud-enable").ClickAsync();
        await Assertions.Expect(page.GetByTestId("cloud-version")).ToHaveCountAsync(1);

        var delete = page.GetByTestId("cloud-delete");
        await Assertions.Expect(delete).ToBeDisabledAsync();
        await page.GetByTestId("cloud-delete-confirm").CheckAsync();
        await delete.ClickAsync();

        await Assertions.Expect(page.GetByTestId("cloud-backup-message")).ToContainTextAsync("Every cloud backup was deleted");
        await Assertions.Expect(page.GetByTestId("cloud-version")).ToHaveCountAsync(0);
    }
}
