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
        // The outer <div data-testid="terminal"> renders before terminal.js's
        // create() call finishes -- xterm.js only creates .xterm-helper-textarea
        // (and only starts accepting focus/input) once that JS interop call
        // completes. TerminalTests.OpenSessionAsync already waits for
        // .xterm-screen (an element xterm.js itself creates) for exactly this
        // reason; this helper didn't, so Keyboard.TypeAsync immediately after
        // "terminal" becomes visible could type into a textarea that does not
        // exist yet and lose the whole string. Diagnosed by typing "tput cols"
        // (used elsewhere in this suite) here without this wait: it reproduced
        // -- a fully blank terminal, no echo at all -- on every run, which is
        // what sent every keyboard-driven test in this file after "exit" or
        // "relayhealth ..." off looking like an unrelated load flake instead.
        await Assertions.Expect(page.Locator(".xterm-screen")).ToBeVisibleAsync();
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

        // Same reason TerminalTests/MultiSessionTests give post-keystroke
        // terminal assertions 10s rather than the 5s default: this round-trips
        // a real keystroke through xterm.js and the Blazor circuit, which the
        // default timeout is too tight for under load.
        await Assertions.Expect(page.GetByTestId("session-relay-health"))
            .ToHaveCountAsync(0, new() { Timeout = 10_000 });
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

        // See RelayHealthBannerClearsWhenTheProblemGoesAwayWithoutReconnecting: a
        // post-keystroke terminal assertion needs the same 10s TerminalTests
        // already uses, not the 5s default.
        await Assertions.Expect(page.GetByTestId("session-relay-health-text"))
            .ToHaveTextAsync(sentinel, new() { Timeout = 10_000 });
    }

    [Fact]
    public async Task RelayHealthBannerReflectsFreshStateAfterReconnectingRatherThanAnEvent()
    {
        var page = await OpenHomeNasAsync();
        await Assertions.Expect(page.GetByTestId("session-relay-health")).ToBeVisibleAsync();

        await page.Keyboard.TypeAsync("exit");
        await page.Keyboard.PressAsync("Enter");
        // Same reason as the other two tests in this file: a post-keystroke
        // terminal assertion needs TerminalTests' 10s, not the 5s default --
        // every observed flake in this test failed here, not at the relay
        // health assertion below, before this timeout was added.
        await Assertions.Expect(page.GetByTestId("session-disconnected"))
            .ToBeVisibleAsync(new() { Timeout = 10_000 });

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
