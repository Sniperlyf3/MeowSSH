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
        await workspace.GetByTestId("open-forwards").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forwards-page")).ToBeVisibleAsync();
        return page;
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

        await workspace.GetByTestId("forward-public-bind").CheckAsync();
        await workspace.GetByTestId("start-forward").ClickAsync();
        await Assertions.Expect(workspace.GetByTestId("forward-row")).ToHaveCountAsync(1);
    }
}
