using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class LaunchSettingsTests(TestHostFixture fixture)
{
    [Fact]
    public async Task SettingsExposeBuildPrivacyAndSupportDestinations()
    {
        var page = await fixture.NewPageAsync();

        await page.GetByTestId("tab-settings").ClickAsync();

        await Assertions.Expect(page.GetByTestId("about-support-settings")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("build-version")).Not.ToBeEmptyAsync();
        await Assertions.Expect(page.GetByTestId("privacy-policy")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("support-link")).ToBeVisibleAsync();
    }
}
