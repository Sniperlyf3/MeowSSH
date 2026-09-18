using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class TailcatPathStatusTests(TestHostFixture fixture)
{
    [Fact]
    public async Task TailcatSessionShowsRelayedPathWithoutTreatingItAsAnError()
    {
        var page = await fixture.NewPageAsync("?multi");
        var homeNas = page.GetByTestId("host-row").Filter(new() { HasText = "home-nas" });

        await homeNas.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        var path = page.GetByTestId("session-path-status");
        await Assertions.Expect(path).ToBeVisibleAsync();
        await Assertions.Expect(path).ToContainTextAsync("Relayed · ci");
        await Assertions.Expect(path).ToHaveAttributeAsync(
            "title",
            "Live Tailcat path. This is connection diagnostics, not relay billing.");
        await Assertions.Expect(path.Locator(".badge")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("badge--neutral"));
    }

    [Fact]
    public async Task OrdinaryTcpSessionDoesNotInventATailcatPath()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-path-status")).ToHaveCountAsync(0);
    }
}
