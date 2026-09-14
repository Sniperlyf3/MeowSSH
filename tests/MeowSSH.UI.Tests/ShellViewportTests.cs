using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class ShellViewportTests(TestHostFixture fixture)
{
    [Fact]
    public async Task LongEditorScrollsOnlyItsContentBetweenPinnedChrome()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.SetViewportSizeAsync(390, 500);

        var body = page.GetByTestId("app-body");
        await Assertions.Expect(body).ToBeVisibleAsync();

        // Force the editor's middle pane to the end. The document itself must
        // remain stationary while the app body owns the scroll position.
        await body.EvaluateAsync("el => el.scrollTo(0, el.scrollHeight)");

        var bodyScrollTop = await body.EvaluateAsync<double>("el => el.scrollTop");
        var windowScrollY = await page.EvaluateAsync<double>("() => window.scrollY");
        Assert.True(bodyScrollTop > 0, "the editor content did not become the scroll container");
        Assert.Equal(0, windowScrollY);

        var topbar = await page.Locator(".topbar").BoundingBoxAsync();
        var tabbar = await page.Locator(".tabbar").BoundingBoxAsync();
        Assert.NotNull(topbar);
        Assert.NotNull(tabbar);

        Assert.InRange(topbar!.Y, 0, 1);
        Assert.InRange(tabbar!.Y + tabbar.Height, 499, 501);
    }
}
