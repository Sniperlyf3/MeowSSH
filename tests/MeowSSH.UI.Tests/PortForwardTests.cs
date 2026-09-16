using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class PortForwardTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenForwardsAsync()
    {
        var page = await fixture.NewPageAsync("/?multi");
        await page.GetByTestId("host-row").Filter(new() { HasText = "prod-web-01" }).ClickAsync();
        var workspace = page.Locator(".session-workspace.is-active");
        await workspace.GetByTestId("session-view-selector").SelectOptionAsync("Forwards");
        await Assertions.Expect(workspace.GetByTestId("forwards-page")).ToBeVisibleAsync();
        return page;
    }

    private static async Task OpenAdvancedAsync(ILocator workspace)
    {
        await workspace.GetByTestId("forward-advanced-options").Locator("summary").ClickAsync();
    }

    [Fact]
    public async Task LocalForwardUsesTheExistingSessionAndShowsAssignedPort()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await workspace.GetByTestId("forward-destination-host").FillAsync("db.internal");
        await workspace.GetByTestId("forward-destination-port").FillAsync("5432");
        await workspace.GetByTestId("start-forward").ClickAsync();

        var row = workspace.GetByTestId("forward-row");
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await Assertions.Expect(row).ToContainTextAsync("Local (-L)");
        await Assertions.Expect(row).ToContainTextAsync("127.0.0.1:42000");
        await Assertions.Expect(row).ToContainTextAsync("db.internal:5432");

        await workspace.GetByTestId("stop-forward").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forwards-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CommonForwardFormKeepsSpecialistControlsCollapsed()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();

        await Assertions.Expect(workspace.GetByTestId("forward-destination-host")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("start-forward")).ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-unix-socket")).Not.ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-public-bind")).Not.ToBeVisibleAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-max-connections")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task SavedProfileCanBeStartedAndDeleted()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("forward-profiles-disclosure").Locator("summary").ClickAsync();
        await workspace.GetByTestId("toggle-forward-profiles").ClickAsync();
        await workspace.GetByTestId("forward-profile-name").FillAsync("Database");
        await workspace.GetByTestId("forward-profile-listen").FillAsync("127.0.0.1:0");
        await workspace.GetByTestId("forward-profile-destination").FillAsync("db.internal:5432");
        await workspace.GetByTestId("save-forward-profile").ClickAsync();

        var profile = workspace.GetByTestId("forward-profile");
        await Assertions.Expect(profile).ToContainTextAsync("Database");
        await Assertions.Expect(profile).ToContainTextAsync("db.internal:5432");

        await profile.GetByTestId("start-forward-profile").ClickAsync();
        var row = workspace.GetByTestId("forward-row");
        await Assertions.Expect(row).ToContainTextAsync("db.internal:5432");
        await Assertions.Expect(row).ToContainTextAsync("127.0.0.1:42000");

        await profile.GetByTestId("delete-forward-profile").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-profiles-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SocksForwardDisplaysGeneratedCredentials()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await workspace.GetByTestId("forward-kind-socks").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-socks-auth")).ToBeCheckedAsync();
        await workspace.GetByTestId("start-forward").ClickAsync();

        var row = workspace.GetByTestId("forward-row");
        await Assertions.Expect(row).ToContainTextAsync("SOCKS5 (-D)");
        await Assertions.Expect(row).ToContainTextAsync("127.0.0.1:42000");
        await Assertions.Expect(workspace.GetByTestId("socks-credentials")).ToContainTextAsync("meowssh");
        await Assertions.Expect(workspace.GetByTestId("socks-credentials")).ToContainTextAsync("test-token");
    }

    [Fact]
    public async Task SocksForwardCanUseCustomCredentials()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await workspace.GetByTestId("forward-kind-socks").ClickAsync();
        await OpenAdvancedAsync(workspace);
        await workspace.GetByTestId("forward-custom-socks-auth").CheckAsync();
        await workspace.GetByTestId("forward-socks-username").FillAsync("cat-user");
        await workspace.GetByTestId("forward-socks-password").FillAsync("cat-secret");
        await workspace.GetByTestId("forward-max-connections").FillAsync("12");
        await workspace.GetByTestId("start-forward").ClickAsync();

        await Assertions.Expect(workspace.GetByTestId("socks-credentials")).ToContainTextAsync("cat-user");
        await Assertions.Expect(workspace.GetByTestId("socks-credentials")).ToContainTextAsync("cat-secret");
    }

    [Fact]
    public async Task LocalForwardCanListenOnAUnixSocket()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await OpenAdvancedAsync(workspace);
        await workspace.GetByTestId("forward-unix-socket").CheckAsync();
        await workspace.GetByTestId("forward-socket-path").FillAsync("/tmp/meowssh-db.sock");
        await workspace.GetByTestId("forward-destination-host").FillAsync("db.internal");
        await workspace.GetByTestId("forward-destination-port").FillAsync("5432");
        await workspace.GetByTestId("start-forward").ClickAsync();

        var row = workspace.GetByTestId("forward-row");
        await Assertions.Expect(row).ToContainTextAsync("/tmp/meowssh-db.sock");
        await Assertions.Expect(row).ToContainTextAsync("db.internal:5432");
    }

    [Fact]
    public async Task RemoteForwardCanBeStartedAlongsideOtherForwards()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await workspace.GetByTestId("start-forward").ClickAsync();

        await workspace.GetByTestId("add-forward").ClickAsync();
        await workspace.GetByTestId("forward-kind-remote").ClickAsync();
        await workspace.GetByTestId("forward-listen-port").FillAsync("8080");
        await workspace.GetByTestId("forward-destination-host").FillAsync("127.0.0.1");
        await workspace.GetByTestId("forward-destination-port").FillAsync("3000");
        await workspace.GetByTestId("start-forward").ClickAsync();

        await Assertions.Expect(workspace.GetByTestId("forward-row")).ToHaveCountAsync(2);
        var remote = workspace.GetByTestId("forward-row").Filter(new() { HasText = "Remote (-R)" });
        await Assertions.Expect(remote).ToContainTextAsync("127.0.0.1:8080");
        await Assertions.Expect(remote).ToContainTextAsync("127.0.0.1:3000");
    }

    [Fact]
    public async Task NonLoopbackLocalBindRequiresExplicitOptIn()
    {
        var page = await OpenForwardsAsync();
        var workspace = page.Locator(".session-workspace.is-active");

        await workspace.GetByTestId("add-first-forward").ClickAsync();
        await workspace.GetByTestId("forward-bind").FillAsync("0.0.0.0");
        await workspace.GetByTestId("start-forward").ClickAsync();

        await Assertions.Expect(workspace.GetByTestId("forward-validation"))
            .ToContainTextAsync("Enable non-loopback binding");
        await Assertions.Expect(workspace.GetByTestId("forward-row")).ToHaveCountAsync(0);

        await OpenAdvancedAsync(workspace);
        await workspace.GetByTestId("forward-public-bind").CheckAsync();
        await workspace.GetByTestId("start-forward").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-row")).ToHaveCountAsync(1);
    }
}
