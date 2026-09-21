using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Regression coverage for audit item A2: a sentinel expiry leaking through the
/// entitlement formatter as a literal date.
/// </summary>
/// <remarks>
/// <see cref="FakeEntitlementService"/> (in MeowSSH.TestHost) always reports a
/// verified Pro grant with <c>ValidUntilUtc = DateTimeOffset.MaxValue</c> -- the
/// exact sentinel the audit found rendered as "verified until 12/31/9999 23:59"
/// in accent-coloured monospace on both Settings screens whose job is to make a
/// paid entitlement feel trustworthy. Every test host session exercises that
/// scenario by construction, so no bespoke fixture setup is needed here.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class EntitlementDisplayTests(TestHostFixture fixture)
{
    [Fact]
    public async Task SettingsRootReadsLifetimeRatherThanTheSentinelDate()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("settings-page")).ToBeVisibleAsync();

        var status = page.GetByTestId("plan-status");
        await Assertions.Expect(status).ToHaveTextAsync("Lifetime");
        await Assertions.Expect(status).Not.ToContainTextAsync("9999");
        await Assertions.Expect(status).Not.ToContainTextAsync("12/31");
    }

    [Fact]
    public async Task PlanSettingsReadLifetimeRatherThanTheSentinelDate()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-plan-settings").ClickAsync();
        await Assertions.Expect(page.GetByTestId("plan-settings")).ToBeVisibleAsync();

        var status = page.GetByTestId("plan-status");
        await Assertions.Expect(status).ToHaveTextAsync("Lifetime");
        await Assertions.Expect(status).Not.ToContainTextAsync("9999");
        await Assertions.Expect(status).Not.ToContainTextAsync("12/31");
        await Assertions.Expect(status).Not.ToContainTextAsync("23:59");
    }
}
