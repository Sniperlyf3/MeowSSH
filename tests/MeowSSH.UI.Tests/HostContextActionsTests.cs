using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class HostContextActionsTests(TestHostFixture fixture)
{
    [Fact]
    public async Task HostEditIsHiddenUntilManageIsOpened()
    {
        var page = await fixture.NewPageAsync("/");
        await Assertions.Expect(page.GetByTestId("manage-host").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("edit-host")).ToHaveCountAsync(0);
        await page.GetByTestId("manage-host").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-manage-actions")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("edit-host")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ManageButtonReportsItsExpandedState()
    {
        var page = await fixture.NewPageAsync("/");
        var manage = page.GetByTestId("manage-host").First;
        await Assertions.Expect(manage).ToHaveAttributeAsync("aria-expanded", "false");
        await manage.ClickAsync();
        await Assertions.Expect(manage).ToHaveAttributeAsync("aria-expanded", "true");
        await manage.ClickAsync();
        await Assertions.Expect(manage).ToHaveAttributeAsync("aria-expanded", "false");
    }

    [Fact]
    public async Task ContextualEditStillOpensTheCorrectHost()
    {
        var page = await fixture.NewPageAsync("/");
        var wrap = page.Locator(".host-wrap").Filter(new() { HasText = "staging-db-replica" });
        await wrap.GetByTestId("manage-host").ClickAsync();
        await wrap.GetByTestId("edit-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("staging-db-replica");
    }

    [Fact]
    public async Task ManageControlKeepsAComfortablePhoneTouchTarget()
    {
        var page = await fixture.NewPageAsync("/");
        var box = await page.GetByTestId("manage-host").First.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 44, $"Manage host width was {box.Width}px.");
        Assert.True(box.Height >= 44, $"Manage host height was {box.Height}px.");
    }

    [Fact]
    public async Task HostDeletionCanBeCancelledWithoutLosingTheConnection()
    {
        var page = await fixture.NewPageAsync("/");
        var wrap = page.Locator(".host-wrap").Filter(new() { HasText = "staging-db-replica" });
        await wrap.GetByTestId("manage-host").ClickAsync();
        await wrap.GetByTestId("edit-host").ClickAsync();
        await page.GetByTestId("delete-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("delete-host-confirmation")).ToBeVisibleAsync();
        await page.GetByTestId("keep-host-connection").ClickAsync();
        await Assertions.Expect(page.GetByTestId("delete-host-confirmation")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("staging-db-replica");
    }
}
