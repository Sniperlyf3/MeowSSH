using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class HostEditorTests(TestHostFixture fixture)
{
    [Fact]
    public async Task TheAddButtonOpensAnEmptyEditor()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("add-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("");
        await Assertions.Expect(page.GetByTestId("delete-host")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CommonConnectionPathIsVisibleWithoutOptionalClutter()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await Assertions.Expect(page.GetByTestId("host-label")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-protocol")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-transport")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-address")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToBeVisibleAsync();

        await Assertions.Expect(page.GetByTestId("host-organisation")).Not.ToHaveAttributeAsync("open", "");
        await Assertions.Expect(page.GetByTestId("host-advanced")).Not.ToHaveAttributeAsync("open", "");
        await Assertions.Expect(page.GetByTestId("host-group")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("jump-host")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task SavingIsRefusedUntilTheHostCanActuallyBeReached()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        var save = page.GetByTestId("save-host");
        await Assertions.Expect(save).ToBeDisabledAsync();

        await page.GetByTestId("host-label").FillAsync("build-01");
        await Assertions.Expect(save).ToBeDisabledAsync();

        await page.GetByTestId("host-address").FillAsync("build.example.com");
        await Assertions.Expect(save).ToBeEnabledAsync();
    }

    [Fact]
    public async Task ASavedHostAppearsInTheList()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("new-machine");
        await page.GetByTestId("host-address").FillAsync("10.9.9.9");

        await page.GetByTestId("save-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("new-machine")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task OrganizationFieldsPersistAndDriveTheHostList()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("organized-host");
        await page.GetByTestId("host-address").FillAsync("10.8.8.8");
        await page.GetByTestId("host-organisation").Locator("summary").ClickAsync();
        await page.GetByTestId("host-group").FillAsync("Operations");
        await page.GetByTestId("host-tags").FillAsync("prod, eu-west, PROD");
        await page.GetByTestId("host-favorite").CheckAsync();

        await page.GetByTestId("save-host").ClickAsync();

        await Assertions.Expect(page.Locator(".section-label").Filter(new() { HasText = "Favorites" })).ToBeVisibleAsync();
        var wrap = page.Locator(".host-wrap").Filter(new() { HasText = "organized-host" });
        await wrap.GetByTestId("edit-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-organisation")).ToHaveAttributeAsync("open", "");
        await Assertions.Expect(page.GetByTestId("host-group")).ToHaveValueAsync("Operations");
        await Assertions.Expect(page.GetByTestId("host-tags")).ToHaveValueAsync("prod, eu-west");
        await Assertions.Expect(page.GetByTestId("host-favorite")).ToBeCheckedAsync();
    }

    [Fact]
    public async Task EditingAHostArrivesWithItsFieldsFilledIn()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("edit-host").First.ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).Not.ToHaveValueAsync("");
        await Assertions.Expect(page.GetByTestId("host-address")).Not.ToHaveValueAsync("");
        await Assertions.Expect(page.GetByTestId("delete-host")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TcpSshOffersAnUpstreamProxyAndValidatesItsScheme()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("proxied-host");
        await page.GetByTestId("host-address").FillAsync("server.internal");
        await page.GetByTestId("host-advanced").Locator("summary").ClickAsync();

        var proxy = page.GetByTestId("ssh-proxy-url");
        await Assertions.Expect(proxy).ToBeVisibleAsync();
        await proxy.FillAsync("https://proxy.example.com:8443");
        await Assertions.Expect(page.GetByTestId("ssh-proxy-validation")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("save-host")).ToBeDisabledAsync();

        await proxy.FillAsync("socks5://127.0.0.1:1080");
        await Assertions.Expect(page.GetByTestId("ssh-proxy-validation")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("save-host")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task NonTcpSshTransportHidesTheUpstreamProxy()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-advanced").Locator("summary").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ssh-proxy-url")).ToBeVisibleAsync();

        await page.GetByTestId("host-transport").SelectOptionAsync("Tailcat");
        await Assertions.Expect(page.GetByTestId("ssh-proxy-url")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ChoosingTailcatHidesTheFieldsItHasNoUseFor()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("host-transport").SelectOptionAsync("Tailcat");

        await Assertions.Expect(page.GetByTestId("host-username")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("host-port")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ChoosingTailscaleExplainsWhyThereIsNoKeyToPick()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("host-transport").SelectOptionAsync("TailscaleSsh");

        await Assertions.Expect(page.GetByTestId("tailscale-note")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ProtocolAndTransportUseStableNativeSelectors()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await Assertions.Expect(page.GetByTestId("host-protocol")).ToHaveValueAsync("Ssh");
        await Assertions.Expect(page.GetByTestId("host-transport")).ToHaveValueAsync("Tcp");

        await page.GetByTestId("host-transport").SelectOptionAsync("Tailcat");
        await Assertions.Expect(page.GetByTestId("host-transport")).ToHaveValueAsync("Tailcat");

        await page.GetByTestId("host-protocol").SelectOptionAsync("Telnet");
        await Assertions.Expect(page.GetByTestId("host-protocol")).ToHaveValueAsync("Telnet");
        await Assertions.Expect(page.GetByTestId("host-transport")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CancellingLeavesTheListUnchanged()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("never-saved");

        await page.GetByTestId("cancel-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("discard-host-draft")).ToBeVisibleAsync();
        await page.GetByTestId("discard-host-draft-confirm").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("never-saved")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DeletingAHostRemovesItFromTheList()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("edit-host").First.ClickAsync();
        var label = await page.GetByTestId("host-label").InputValueAsync();

        await page.GetByTestId("delete-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(label, new() { Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AHostnameIsNotUppercasedAsItIsTyped()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        var address = page.GetByTestId("host-address");
        await address.FillAsync("Build.Example.COM");

        var transform = await address.EvaluateAsync<string>("el => getComputedStyle(el).textTransform");
        Assert.Equal("none", transform);
        await Assertions.Expect(address).ToHaveValueAsync("Build.Example.COM");
    }
}
