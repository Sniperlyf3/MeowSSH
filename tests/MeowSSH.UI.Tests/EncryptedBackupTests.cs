using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class EncryptedBackupTests(TestHostFixture fixture)
{
    [Fact]
    public async Task BackupTabExplainsEncryptionAndExportsThroughLocalFileBridge()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("tab-backup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("encrypted-backup-page")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("encrypted-backup-page"))
            .ToContainTextAsync("never written as plaintext");

        await page.GetByTestId("backup-export").ClickAsync();

        await Assertions.Expect(page.GetByTestId("backup-message"))
            .ToContainTextAsync("Encrypted backup saved to");
    }

    [Fact]
    public async Task ImportCancellationDoesNotShowDestructiveRestoreForm()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-backup").ClickAsync();

        await page.GetByTestId("backup-import").ClickAsync();

        await Assertions.Expect(page.GetByTestId("backup-restore-form")).ToHaveCountAsync(0);
    }
}
