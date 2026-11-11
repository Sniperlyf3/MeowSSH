using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The public-relay rule as the user sees it: asked before a Tailcat host is
/// reached over the public tailcat.dev relay, the answer kept on the host and
/// editable there, a refusal that leads straight to that setting, and a "Who
/// can connect?" choice when sharing.
/// </summary>
/// <remarks>
/// The real <c>TailcatRelaySshEngine</c> and policy run in front of the fake
/// transport. "publicrelay" in the query string makes the fake hub's relay the
/// public one (<see cref="MeowSSH.TestHost.Fakes.FakeTailcatRelayDirectory"/>);
/// without it, it is MeowSSH's own.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class PublicRelayTests(TestHostFixture fixture)
{
    private static ILocator HomeNas(IPage page) => page.GetByTestId("host-row").Filter(new() { HasText = "home-nas" });

    [Fact]
    public async Task AHostOnMeowSshsRelayConnectsWithoutAQuestion()
    {
        var page = await fixture.NewPageAsync("?multi");
        await HomeNas(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("public-relay-prompt")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task APublicRelayHostAsksFirstAndConnectOnceConnects()
    {
        var page = await fixture.NewPageAsync("?multi&publicrelay");
        await HomeNas(page).ClickAsync();

        var prompt = page.GetByTestId("public-relay-prompt");
        await Assertions.Expect(prompt).ToBeVisibleAsync();
        await Assertions.Expect(prompt).ToContainTextAsync("home-nas");
        await Assertions.Expect(page.GetByTestId("public-relay-always")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("public-relay-save-hint")).ToHaveCountAsync(0);

        await page.GetByTestId("public-relay-once").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(prompt).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DecliningLeadsToTheHostSettingAndChangingItStopsTheQuestion()
    {
        var page = await fixture.NewPageAsync("?multi&publicrelay");
        await HomeNas(page).ClickAsync();
        // Fully on screen at phone height: the "Connecting" sheet used to cover it.
        await Assertions.Expect(page.GetByTestId("connecting")).ToHaveCountAsync(0);
        await page.GetByTestId("public-relay-cancel").ClickAsync();

        var error = page.GetByTestId("connect-error");
        await Assertions.Expect(error).ToContainTextAsync("Public relay not allowed");
        await page.GetByTestId("open-host-settings").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        var setting = page.GetByTestId("host-public-relay");
        await Assertions.Expect(setting).ToHaveValueAsync("Ask");
        await setting.SelectOptionAsync("Always");
        await page.GetByTestId("save-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);

        await HomeNas(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("public-relay-prompt")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AlwaysForThisHostIsKeptAndShownInItsSettings()
    {
        var page = await fixture.NewPageAsync("?multi&publicrelay");
        await HomeNas(page).ClickAsync();
        await page.GetByTestId("public-relay-always").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        await page.GetByTestId("new-session-tab").ClickAsync();
        var wrap = page.Locator(".host-wrap").Filter(new() { HasText = "home-nas" });
        await wrap.GetByTestId("manage-host").ClickAsync();
        await wrap.GetByTestId("edit-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-public-relay")).ToHaveValueAsync("Always");
    }

    [Fact]
    public async Task NeverRefusesWithoutAsking()
    {
        var page = await fixture.NewPageAsync("?multi&publicrelay");
        var wrap = page.Locator(".host-wrap").Filter(new() { HasText = "home-nas" });
        await wrap.GetByTestId("manage-host").ClickAsync();
        await wrap.GetByTestId("edit-host").ClickAsync();
        await page.GetByTestId("host-public-relay").SelectOptionAsync("Never");
        await page.GetByTestId("save-host").ClickAsync();

        await HomeNas(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("connect-error")).ToContainTextAsync("Public relay");
        await Assertions.Expect(page.GetByTestId("public-relay-prompt")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("open-host-settings")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task OnlyTailcatHostsHaveTheSetting()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await Assertions.Expect(page.GetByTestId("host-public-relay")).ToHaveCountAsync(0);
        await page.GetByTestId("host-transport").SelectOptionAsync("Tailcat");
        await Assertions.Expect(page.GetByTestId("host-public-relay")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-public-relay")).ToHaveValueAsync("Ask");
        await Assertions.Expect(page.GetByTestId("host-tailcat-derpmap")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SharingDefaultsToMeowSshDevicesAndWarnsBeforeSharingWithAnyone()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-section-phone").ClickAsync();

        var audience = page.GetByTestId("tailcat-server-audience");
        await Assertions.Expect(audience).ToHaveValueAsync("mine");
        await Assertions.Expect(page.GetByTestId("tailcat-server-audience-public-warning")).ToHaveCountAsync(0);
        await audience.SelectOptionAsync("anyone");
        await Assertions.Expect(page.GetByTestId("tailcat-server-audience-public-warning")).ToBeVisibleAsync();

        await page.GetByTestId("tailcat-section-share").ClickAsync();
        var temporary = page.GetByTestId("tailcat-temporary-share-audience");
        await Assertions.Expect(temporary).ToHaveValueAsync("mine");
        await temporary.SelectOptionAsync("anyone");
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-audience-public-warning")).ToBeVisibleAsync();
    }
}
