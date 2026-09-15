using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TailcatCompletionTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTailcatAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task TailcatHubExposesVpnSection()
    {
        var page = await OpenTailcatAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-section-vpn")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PortForwardCanUseUdp()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-connect").ClickAsync();
        await page.GetByTestId("tailcat-forward-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-forward-mappings").FillAsync("5353:53");
        await page.GetByTestId("tailcat-forward-udp").CheckAsync();
        await page.GetByTestId("start-tailcat-forward").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToContainTextAsync("UDP");
        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToContainTextAsync("5353:53");
    }

    [Fact]
    public async Task PhoneCanServeArbitraryTargetsWithoutBuiltInService()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-exit-node").UncheckAsync();
        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("tailcat-serve-targets").FillAsync("80,443,8000-8010");

        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeEnabledAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-running")).ToContainTextAsync("Services: 80, 443, 8000-8010");
    }

    [Fact]
    public async Task ServeAllShowsExposureWarning()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-serve-targets").FillAsync("all");
        await Assertions.Expect(page.GetByTestId("tailcat-serve-all-warning")).ToContainTextAsync("every reachable local service");
    }

    [Fact]
    public async Task TailcatQrCanBeScannedAndValidated()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-address").ClickAsync();
        await page.GetByTestId("tailcat-scan-address-qr").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-address-input")).ToHaveValueAsync("tc-test-scanned-address");
        await Assertions.Expect(page.GetByTestId("tailcat-resolved-address")).ToHaveValueAsync("tc-test-resolved-full-address");
        await Assertions.Expect(page.GetByTestId("tailcat-address-details")).ToContainTextAsync("nodekey:test-server");
    }

    [Fact]
    public async Task FullDeviceTailcatVpnCanStartAndStop()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-vpn").ClickAsync();
        await page.GetByTestId("tailcat-vpn-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-vpn-start").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-vpn-running")).ToContainTextAsync("0.0.0.0/0");
        await page.GetByTestId("tailcat-vpn-stop").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-vpn-start")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TailcatVpnCanRouteSelectedSubnets()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-vpn").ClickAsync();
        await page.GetByTestId("tailcat-vpn-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-vpn-full-device").UncheckAsync();
        await page.GetByTestId("tailcat-vpn-routes").FillAsync("192.168.50.0/24\n10.42.0.0/16");
        await page.GetByTestId("tailcat-vpn-start").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-vpn-running")).ToContainTextAsync("192.168.50.0/24");
        await Assertions.Expect(page.GetByTestId("tailcat-vpn-running")).ToContainTextAsync("10.42.0.0/16");
    }
}
