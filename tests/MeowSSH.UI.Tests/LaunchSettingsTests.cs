using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class LaunchSettingsTests(TestHostFixture fixture)
{
    [Fact]
    public async Task SettingsExposeBuildPrivacyTermsSupportAndThirdPartyNotices()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();
        await page.GetByTestId("open-about-settings").ClickAsync();

        await Assertions.Expect(page.GetByTestId("about-support-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("build-version")).Not.ToBeEmptyAsync();
        await Assertions.Expect(page.GetByTestId("privacy-policy")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("terms-of-service")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("support-link")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("third-party-notices")).ToBeVisibleAsync();

        await page.GetByTestId("third-party-notices").ClickAsync();
        var notices = page.GetByTestId("third-party-notices-content");
        await Assertions.Expect(notices).ToContainTextAsync("Tailcat — BSD 3-Clause");
        await Assertions.Expect(notices).ToContainTextAsync("not affiliated with, sponsored by, or endorsed by Tailscale Inc.");
    }

    [Fact]
    public async Task SettingsRootStaysACompactCategoryNavigator()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();

        await Assertions.Expect(page.GetByTestId("open-plan-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("open-appearance-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("open-session-log-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("open-about-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("refresh-entitlement")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("session-log-auto-record")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("privacy-policy")).ToHaveCountAsync(0);
    }
}
