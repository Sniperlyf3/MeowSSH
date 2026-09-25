using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class PlanUpgradeTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenPlanAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-plan-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("plan-settings")).ToBeVisibleAsync();
        return page;
    }

    private static ILocator Offers(IPage page) => page.Locator("[data-testid^='purchase-']");

    [Fact]
    public async Task FreeSeesEveryOffer()
    {
        var page = await OpenPlanAsync("free");

        await Assertions.Expect(Offers(page)).ToHaveCountAsync(4);
        await Assertions.Expect(page.GetByTestId("purchase-meowssh_pro_lifetime")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ProCanMoveUpToProCloudOrTeamButIsNotOfferedProAgain()
    {
        var page = await OpenPlanAsync("");

        await Assertions.Expect(page.GetByTestId("purchase-meowssh_pro_cloud-monthly")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("purchase-meowssh_team-monthly")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("purchase-meowssh_pro_lifetime")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ProCloudIsOfferedTeamOnly()
    {
        var page = await OpenPlanAsync("procloud");

        await Assertions.Expect(Offers(page)).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("purchase-meowssh_team-monthly")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task TeamIsOfferedNothing()
    {
        var page = await OpenPlanAsync("teamtier");

        await Assertions.Expect(page.GetByTestId("restore-purchases")).ToBeVisibleAsync();
        await Assertions.Expect(Offers(page)).ToHaveCountAsync(0);
    }
}
