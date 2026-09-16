using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class EncryptedBackupTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenBackupAsync()
    {
        var page = await fixture.NewPageAsync();
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
        var page = await OpenBackupAsync();

        await page.GetByTestId("backup-import").ClickAsync();

        await Assertions.Expect(page.GetByTestId("backup-restore-form")).ToHaveCountAsync(0);
    }
}
