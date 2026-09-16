using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class TailcatHubTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTailcatAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task TailcatTabExposesAllFeatureSections()
    {
        var page = await OpenTailcatAsync();
        foreach (var section in new[] { "phone", "connect", "transfers", "address", "keys", "diagnostics" })
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
    public async Task SavedTailcatIdentityCanBeSelectedAsAllowedClient()
    {
        var page = await OpenTailcatAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-saved-client-keys")).ToContainTextAsync("client-default");
        await page.GetByTestId("tailcat-saved-client-key").First.CheckAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeEnabledAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-address")).ToHaveValueAsync("tc-test-address");
    }

    [Fact]
    public async Task ServerCanReuseSavedPersistentIdentity()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("tailcat-server-address-options").ClickAsync();
        await page.GetByTestId("tailcat-server-identity").SelectOptionAsync("client-default");
        await Assertions.Expect(page.GetByTestId("tailcat-persistent-identity-warning")).ToBeVisibleAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-address")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ServerCanPublishASelfContainedAddress()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("tailcat-server-address-options").ClickAsync();
        await page.GetByTestId("tailcat-full-address").CheckAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-address")).ToHaveValueAsync("tc-test-full-address");
    }

    [Fact]
    public async Task InsecureExitNodeRequiresExplicitRiskAcknowledgement()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-insecure-exit-node").CheckAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-insecure-warning")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-insecure-warning")).ToContainTextAsync("removes Tailcat client authentication");
        await Assertions.Expect(page.GetByTestId("tailcat-allowed-clients")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeDisabledAsync();
        await page.GetByTestId("tailcat-insecure-confirm").CheckAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeEnabledAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-running")).ToContainTextAsync("INSECURE: any Tailcat client");
    }

    [Fact]
    public async Task ShellCanUseTailcatCredentialInsteadOfSshKeysWithWarning()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-exit-node").UncheckAsync();
        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("tailcat-shell").CheckAsync();
        await page.GetByTestId("tailcat-insecure-shell").CheckAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-authorized-ssh")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-insecure-shell-warning")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-insecure-shell-warning")).ToContainTextAsync("Tailcat address is the shell credential");
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeDisabledAsync();
        await page.GetByTestId("tailcat-insecure-shell-confirm").CheckAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeEnabledAsync();
        await page.GetByTestId("start-tailcat-server").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-server-running")).ToContainTextAsync("Shell (Tailcat credential only)");
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
    public async Task BrowseShortcutCreatesALocalWebForward()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-connect").ClickAsync();
        await page.GetByTestId("tailcat-forward-address").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-browse").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-forward-row")).ToContainTextAsync("0:80");
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
    public async Task AddressBuilderResolvesInspectsAndClassifiesTailcatAddresses()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-address").ClickAsync();
        await page.GetByTestId("tailcat-address-input").FillAsync("tc-short-address");
        await page.GetByTestId("tailcat-resolve-address").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-resolved-address")).ToHaveValueAsync("tc-test-resolved-full-address");
        await Assertions.Expect(page.GetByTestId("tailcat-address-details")).ToContainTextAsync("nodekey:test-server");
        await Assertions.Expect(page.GetByTestId("tailcat-address-region")).ToContainTextAsync("Test DERP");
        await Assertions.Expect(page.GetByTestId("tailcat-address-node")).ToContainTextAsync("derp.example.test");
        await Assertions.Expect(page.GetByTestId("tailcat-relay-class")).ToHaveTextAsync("Public Tailcat relay");
        await Assertions.Expect(page.GetByTestId("tailcat-public-relay-warning")).ToBeVisibleAsync();

        await page.GetByTestId("tailcat-address-derpmap").FillAsync("https://relay.example.test/derpmap.json");
        await page.GetByTestId("tailcat-inspect-address").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-relay-class")).ToHaveTextAsync("Your relay");
        await Assertions.Expect(page.GetByTestId("tailcat-user-relay-note")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TailcatAddressCanBeShownAsAnOfflineQrCode()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-address").ClickAsync();
        await page.GetByTestId("tailcat-address-input").FillAsync("tc-test-address");
        await page.GetByTestId("tailcat-show-address-qr").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-address-qr")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-address-qr").Locator("svg")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("tailcat-address-qr")).ToContainTextAsync("Keep this QR private");
    }

    [Fact]
    public async Task SavedTailcatHostsAreReusableAsPeers()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-address").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-address-saved-peer")).ToContainTextAsync("home-nas");
        await page.GetByTestId("tailcat-address-saved-peer").SelectOptionAsync(new SelectOptionValue { Label = "home-nas" });
        await Assertions.Expect(page.GetByTestId("tailcat-address-input")).Not.ToHaveValueAsync("");
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
    public async Task PrivateTailcatIdentityCanBeRevealedForBackup()
    {
        var page = await OpenTailcatAsync();
        await page.GetByTestId("tailcat-section-keys").ClickAsync();
        await page.GetByTestId("tailcat-export-key").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-export-json")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("privkey:test"));
        await Assertions.Expect(page.GetByTestId("tailcat-identity-portability")).ToContainTextAsync("Private identity backup");
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
