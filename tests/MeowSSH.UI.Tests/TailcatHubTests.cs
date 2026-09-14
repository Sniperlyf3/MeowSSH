using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TailcatHubTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTailcatAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task TailcatTabExposesAllFeatureSections()
    {
        var page = await OpenTailcatAsync();

        foreach (var section in new[] { "phone", "connect", "transfers", "keys", "diagnostics" })
            await Assertions.Expect(page.GetByTestId($"tailcat-section-{section}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ExitNodeRequiresAnAllowedClientAndCanBeStopped()
    {
        var page = await OpenTailcatAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeDisabledAsync();

        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("start-tailcat-server").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-server-address")).ToHaveValueAsync("tc-test-address");
        await page.GetByTestId("stop-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SocksGatewayAndPortForwardCanRunTogether()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-connect").ClickAsync();

        await page.GetByTestId("start-tailcat-socks").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-socks-running")).ToContainTextAsync("127.0.0.1:19080");

        await page.GetByTestId("tailcat-forward-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-forward-mappings").FillAsync("8080:80");
        await page.GetByTestId("start-tailcat-forward").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToContainTextAsync("127.0.0.1:18080");
    }

    [Fact]
    public async Task TransfersBrowseRemoteFiles()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-transfers").ClickAsync();
        await page.GetByTestId("tailcat-files-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-list-files").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-file-row")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("tailcat-file-row").Last).ToContainTextAsync("hello.txt");
    }

    [Fact]
    public async Task KeyManagerGeneratesAClientIdentity()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-keys").ClickAsync();
        await page.GetByTestId("tailcat-key-name").FillAsync("friend-phone");
        await page.GetByTestId("tailcat-key-type").SelectOptionAsync("true");
        await page.GetByTestId("tailcat-generate-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-generated-key")).ToContainTextAsync("nodekey:test-client");
    }

    [Fact]
    public async Task DiagnosticsReportsDirectConnectivity()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-diagnostics").ClickAsync();
        await page.GetByTestId("tailcat-diagnostic-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-diagnose").ClickAsync();

        var result = page.GetByTestId("tailcat-diagnostic-result");
        await Assertions.Expect(result).ToContainTextAsync("Direct");
        await Assertions.Expect(result).ToContainTextAsync("18 ms");
        await Assertions.Expect(result).ToContainTextAsync("nodekey:test-server");
    }
}
