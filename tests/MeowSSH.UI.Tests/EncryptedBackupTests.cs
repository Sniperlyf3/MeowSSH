using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class EncryptedBackupTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenBackupAsync(string query = "")
    {
        var page = await fixture.NewPageAsync(query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-backup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("encrypted-backup-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task BackupTabExplainsEncryptionAndExportsThroughLocalFileBridge()
    {
        var page = await OpenBackupAsync();

        await Assertions.Expect(page.GetByTestId("encrypted-backup-page"))
            .ToContainTextAsync("never written as plaintext");

        await page.GetByTestId("backup-export").ClickAsync();

        await Assertions.Expect(page.GetByTestId("backup-message"))
            .ToContainTextAsync("Encrypted backup saved to");
    }

    [Fact]
    public async Task ImportCancellationDoesNotShowDestructiveRestoreForm()
    {
        // "pickcancel": the user backs out of the file picker. This used to run
        // with a picker that always returned a file, and passed only because
        // the count was checked before the form had rendered.
        var page = await OpenBackupAsync("?pickcancel");

        await page.GetByTestId("backup-import").ClickAsync();

        // Settled: the button is usable again, so the pick has finished.
        await Assertions.Expect(page.GetByTestId("backup-import")).ToBeEnabledAsync();
        await Assertions.Expect(page.GetByTestId("backup-restore-form")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RestoringNeedsTheRecoveryCodeAndAnExplicitReplaceThenOffersRebinding()
    {
        var page = await OpenBackupAsync();
        await page.GetByTestId("backup-import").ClickAsync();

        var form = page.GetByTestId("backup-restore-form");
        await Assertions.Expect(form).ToContainTextAsync("start.sh");
        await Assertions.Expect(form).ToContainTextAsync("schema 3 · revision 7");
        var restore = page.GetByTestId("backup-restore");
        await Assertions.Expect(restore).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("backup-recovery-code")).ToHaveAttributeAsync("type", "password");
        await page.GetByTestId("backup-recovery-code").FillAsync("ABCD-EFGH-IJKL");
        // A code alone is not consent to replace this device's vault.
        await Assertions.Expect(restore).ToBeDisabledAsync();
        await page.GetByTestId("backup-confirm-replace").CheckAsync();
        await restore.ClickAsync();

        await Assertions.Expect(page.GetByTestId("backup-message")).ToHaveTextAsync("Encrypted vault restored. Enable biometric unlock for this device when ready.");
        await Assertions.Expect(form).ToHaveCountAsync(0);
        await page.GetByTestId("backup-rebind").ClickAsync();
        await Assertions.Expect(page.GetByTestId("backup-message")).ToHaveTextAsync("Biometric/device unlock is now bound to the restored vault.");
        await Assertions.Expect(page.GetByTestId("backup-rebind-section")).ToHaveCountAsync(0);
    }
}
