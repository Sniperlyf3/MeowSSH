using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Coverage for the app-facing half of the managed-DERP quota chain: when a Free
/// user's monthly managed-relay allowance is exceeded, the relay's refusal reason
/// (a <c>derp.FrameHealth</c> message) reaches <c>MeowshellAgentConnection.CurrentRelayHealth</c>
/// / <c>RelayHealthChanged</c>, and this is what shows it in the active session
/// rather than letting the user see a generic connection failure.
/// </summary>
/// <remarks>
/// <para>
/// The contract (set on the Meowshell side) is that <c>null</c> means healthy --
/// both "never had a problem" and "the problem just cleared" collapse to
/// <c>null</c> on purpose -- and a non-null value is literal, pre-formatted text
/// from the relay, never a code to map. <see cref="MeowSSH.TestHost.Fakes.FakeSshEngine"/>
/// mirrors that on the Tailcat host ("home-nas") only, gated behind "relayhealth"
/// in the query string, same piggyback on query parsing as
/// <see cref="MeowSSH.TestHost.Fakes.FakeEntitlementService"/>'s "free".
/// </para>
/// <para>
/// Unlike the PathChanged fake this project already has (which replays the
/// current value the instant something subscribes),
/// <see cref="MeowSSH.TestHost.Fakes.FakeSshEngine"/>'s RelayHealthChanged
/// deliberately does *not* replay on subscribe. That is what makes
/// <see cref="RelayHealthBannerAppearsWithTheLiteralRelayTextOnLoad"/> and
/// <see cref="RelayHealthBannerReflectsFreshStateAfterReconnectingRatherThanAnEvent"/>
/// real regression tests for the "read the property before subscribing" half of
/// the contract, not just tests that a change event fires: if AppRoot ever
/// started relying on the event alone, no event would ever come for these two
/// scenarios and both would fail.
/// </para>
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class RelayHealthBannerTests(TestHostFixture fixture)
{
    private const string OverQuotaMessage = "MeowSSH managed relay: monthly usage allowance exceeded";

    private async Task<IPage> OpenHomeNasAsync(string query = "?multi&relayhealth")
    {
        var page = await fixture.NewPageAsync(query);
        var homeNas = page.GetByTestId("host-row").Filter(new() { HasText = "home-nas" });
        await homeNas.ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task RelayHealthBannerAppearsWithTheLiteralRelayTextOnLoad()
    {
        var page = await OpenHomeNasAsync();

        var banner = page.GetByTestId("session-relay-health");
        await Assertions.Expect(banner).ToBeVisibleAsync();
        // The literal string, not a paraphrase or a mapped code: this is exactly
        // the "MeowshellAgentConnection.CurrentRelayHealth" contract text a real
        // admission refusal would carry.
        await Assertions.Expect(page.GetByTestId("session-relay-health-text")).ToHaveTextAsync(OverQuotaMessage);

        // Free-tier principle: the banner must not just name the problem, it must
        // say what still works and where to check the allowance/reset state
        // rather than duplicating the byte counters that already live in
        // Tools -> Tailcat -> Managed relay usage.
        await Assertions.Expect(banner).ToContainTextAsync("Direct connections and self-hosted relays are not affected");
        await Assertions.Expect(banner).ToContainTextAsync("Managed relay usage");
    }

    [Fact]
    public async Task RelayHealthBannerIsAbsentWhenTheManagedRelayIsHealthy()
    {
        var page = await OpenHomeNasAsync("?multi");

        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RelayHealthBannerClearsWhenTheProblemGoesAwayWithoutReconnecting()
    {
        var page = await OpenHomeNasAsync();
        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToBeVisibleAsync();

        // Drives FakeSshConnection.RelayHealthChanged(null) through the live
        // shell (see FakeSshShell's "relayhealth ..." hook) -- a genuine
        // non-null-to-null transition on the same connection, not a fresh page.
        await page.Keyboard.TypeAsync("relayhealth clear");
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task RelayHealthBannerRendersArbitraryTextVerbatimRatherThanMappingIt()
    {
        // Starts healthy so the only way the banner can appear is the change
        // event below, carrying a string that resembles nothing MeowSSH ships
        // canned copy for -- proving the text is passed straight through.
        var page = await OpenHomeNasAsync("?multi");
        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToHaveCountAsync(0);

        const string sentinel = "zzz-not-a-known-relay-phrase-42";
        await page.Keyboard.TypeAsync($"relayhealth {sentinel}");
        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(page.GetByTestId("session-relay-health-text")).ToHaveTextAsync(sentinel);
    }

    [Fact]
    public async Task RelayHealthBannerReflectsFreshStateAfterReconnectingRatherThanAnEvent()
    {
        var page = await OpenHomeNasAsync();
        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToBeVisibleAsync();

        await page.Keyboard.TypeAsync("exit");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("session-disconnected")).ToBeVisibleAsync();

        await page.GetByTestId("session-reconnect").ClickAsync();
        await Assertions.Expect(page.GetByTestId("terminal")).ToBeVisibleAsync();

        // The reconnected connection is a brand new FakeSshConnection instance;
        // nothing carries the old one's state over and its RelayHealthChanged
        // never fires by itself. This can only be visible again if AppRoot
        // re-read RelayHealth off the new connection the same way it did on the
        // very first connect.
        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-relay-health-text")).ToHaveTextAsync(OverQuotaMessage);
    }
}
