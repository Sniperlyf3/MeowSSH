using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class HardwareKeyUiTests(TestHostFixture fixture)
{
    private static async Task OpenDeviceKeyFlowAsync(IPage page)
    {
        await page.GetByTestId("add-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToBeVisibleAsync();
        await page.GetByTestId("flow-device-key").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-editor")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DeviceKeyCanBeGeneratedSavedCopiedAndSelectedForAHost()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenDeviceKeyFlowAsync(page);
        await page.GetByTestId("credential-label").FillAsync("phone deploy key");

        await Assertions.Expect(page.GetByTestId("hardware-key-explanation")).ToContainTextAsync("cannot read or export");
        await Assertions.Expect(page.GetByTestId("save-credential")).ToBeDisabledAsync();

        await page.GetByTestId("generate-hardware-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("generated-hardware-key-status")).ToContainTextAsync("Hardware-backed");
        await Assertions.Expect(page.GetByTestId("generated-hardware-public-key"))
            .ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^ecdsa-sha2-nistp256 "));
        await Assertions.Expect(page.GetByTestId("save-credential")).ToBeEnabledAsync();

        await page.GetByTestId("save-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("credential-row")).ToContainTextAsync("phone deploy key");
        await Assertions.Expect(page.GetByTestId("credential-row")).ToContainTextAsync("Device key");
        await Assertions.Expect(page.GetByTestId("hardware-key-status")).ToContainTextAsync("Hardware-backed");
        await Assertions.Expect(page.GetByText("BEGIN PRIVATE KEY")).ToHaveCountAsync(0);

        await Assertions.Expect(page.GetByTestId("show-public-key")).Not.ToBeVisibleAsync();
        await page.GetByTestId("manage-credential").Locator("summary").ClickAsync();
        await page.GetByTestId("show-public-key").ClickAsync();
        await Assertions.Expect(page.GetByTestId("public-key-value"))
            .ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^ecdsa-sha2-nistp256 "));

        await page.GetByTestId("tab-hosts").ClickAsync();
        await page.GetByTestId("add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToContainTextAsync("phone deploy key");
    }

    [Fact]
    public async Task DeletingDeviceKeyRequiresOpeningManage()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenDeviceKeyFlowAsync(page);
        await page.GetByTestId("credential-label").FillAsync("temporary device key");
        await page.GetByTestId("generate-hardware-key").ClickAsync();
        await page.GetByTestId("save-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("delete-credential")).Not.ToBeVisibleAsync();
        await page.GetByTestId("manage-credential").Locator("summary").ClickAsync();
        await page.GetByTestId("delete-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();
    }
}
