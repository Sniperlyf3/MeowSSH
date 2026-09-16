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
}
