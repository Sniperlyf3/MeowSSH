using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Coverage for the C2 (host name truncation) and C7 (empty states) items in
/// <c>docs/specs/ui-ux-audit-2026-09.md</c>. <see cref="UiUxAuditFollowUpTests"/>
/// already pins what the earlier CSS-only pass achieved; these pin the two
/// behaviours added on top of it -- collapsing the status badge to a dot on a
/// tight row, and scrolling a truncated name into view while its row is
/// focused -- plus the empty states that previously let a user walk into a
/// flow that could only fail.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class HostNameOverflowAndEmptyStateTests(TestHostFixture fixture)
{
    // C2: dot-only badges ---------------------------------------------------

    /// <summary>
    /// Below the 460px breakpoint the badge keeps its dot and loses its
    /// visible label, reclaiming width for the name column. The label is
    /// clipped rather than removed, so this asserts on geometry (a ~1px box)
    /// and not on absence -- a fix that used display:none would pass a
    /// "not visible" assertion while silently taking the word out of the
    /// accessibility tree, which is the thing this must not do.
    /// </summary>
    [Fact]
    public async Task HostMetaDotOnlyBreakpointCollapsesTheBadgeToADot()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        var badge = page.GetByTestId("host-row").First.Locator(".badge");
        var label = badge.Locator(".badge__label");

        var labelWidth = await label.EvaluateAsync<double>("el => el.getBoundingClientRect().width");
        Assert.True(labelWidth <= 2,
            $"Expected the badge label clipped to ~1px below the breakpoint, got {labelWidth}px.");

        // Still in the accessibility tree, and still carrying the real word.
        await Assertions.Expect(label).Not.ToBeEmptyAsync();
        Assert.False(await label.EvaluateAsync<bool>("el => getComputedStyle(el).display === 'none'"),
            "The label must be clipped, not display:none -- display:none removes it from the a11y tree.");

        // The dot alone is visible, so the badge carries a title for a
        // sighted pointer user.
        await Assertions.Expect(badge).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex(".+"));
        await Assertions.Expect(badge.Locator(".badge__dot")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Above the breakpoint nothing changes: the label is a normal, visible
    /// part of the badge. Without this, collapsing every badge everywhere
    /// would pass the test above.
    /// </summary>
    [Fact]
    public async Task HostBadgeKeepsItsVisibleLabelAboveTheBreakpoint()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(461, 915);

        var label = page.GetByTestId("host-row").First.Locator(".badge .badge__label");
        var width = await label.EvaluateAsync<double>("el => el.getBoundingClientRect().width");

        Assert.True(width > 10, $"Expected a normally laid-out badge label above 460px, got {width}px.");
    }

    // C2: scroll on focus ---------------------------------------------------

    /// <summary>
    /// Focusing a row whose name is truncated starts the marquee and sets a
    /// positive travel distance; blurring it stops. The distance assertion is
    /// the point: the first implementation measured the inline text span's
    /// scrollWidth, which is defined as 0 for a non-replaced inline box, so
    /// the distance was always negative and the marquee could never start.
    /// A test that only asserted "the class is present" would not have caught
    /// that, because the class was never added at all.
    /// </summary>
    [Fact]
    public async Task ATruncatedHostNameScrollsIntoViewWhileItsRowIsFocused()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        var row = page.GetByTestId("host-row").Filter(new() { HasTextString = "staging-db-replica-eu-west" });
        var name = row.Locator(".host__name");

        await row.FocusAsync();
        await Assertions.Expect(name).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("host__name--marquee"));

        var distance = await name.EvaluateAsync<double>(
            "el => parseFloat(getComputedStyle(el).getPropertyValue('--marquee-distance')) || 0");
        Assert.True(distance > 0,
            $"Expected a positive marquee distance for a truncated name, got {distance}px. " +
            "0 or negative means the overflow was measured on the wrong element again.");

        await page.Locator("body").EvaluateAsync("() => document.activeElement.blur()");
        await Assertions.Expect(name).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("host__name--marquee"));
    }

    /// <summary>
    /// A name that already fits never animates, however it is focused.
    /// </summary>
    [Fact]
    public async Task AHostNameThatFitsDoesNotScrollWhenFocused()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        var row = page.GetByTestId("host-row")
            .Filter(new() { HasNotTextString = "staging-db-replica-eu-west" }).First;
        await row.FocusAsync();

        await Assertions.Expect(row.Locator(".host__name"))
            .Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("host__name--marquee"));
    }

    /// <summary>
    /// prefers-reduced-motion means no marquee, even on a row that would
    /// otherwise qualify. The name stays ellipsised, which is the pre-C2
    /// behaviour and an acceptable resting state.
    /// </summary>
    [Fact]
    public async Task ATruncatedHostNameDoesNotScrollWhenReducedMotionIsRequested()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });

        var row = page.GetByTestId("host-row").Filter(new() { HasTextString = "staging-db-replica-eu-west" });
        await row.FocusAsync();

        await Assertions.Expect(row.Locator(".host__name"))
            .Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("host__name--marquee"));
    }

    // C7: empty states ------------------------------------------------------

    /// <summary>
    /// Actions distinguishes "no hosts saved" from "no Actions saved". An
    /// Action must target a saved host, so offering the create flow with an
    /// empty vault walks the user into a form they cannot submit.
    /// </summary>
    [Fact]
    public async Task ActionsOffersAddingAHostRatherThanCreatingAnActionWhenTheVaultIsEmpty()
    {
        var page = await fixture.NewPageAsync("/?nohosts");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();

        await Assertions.Expect(page.GetByTestId("actions-no-hosts")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("actions-empty")).Not.ToBeVisibleAsync();

        // Lands on the Hosts tab, which with an empty vault is its own
        // empty state rather than a list.
        await page.GetByTestId("actions-add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("empty-state")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("add-first-host")).ToBeVisibleAsync();
    }

    /// <summary>
    /// With hosts present it is the ordinary "no Actions yet" state, so the
    /// new branch cannot swallow the existing one.
    /// </summary>
    [Fact]
    public async Task ActionsShowsItsOrdinaryEmptyStateWhenHostsExist()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-actions").ClickAsync();

        await Assertions.Expect(page.GetByTestId("actions-empty")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("actions-no-hosts")).Not.ToBeVisibleAsync();
    }

    /// <summary>
    /// Host Health said nothing until the user pressed its only button and
    /// waited for a run that could only report "No saved hosts are available
    /// to check".
    /// </summary>
    [Fact]
    public async Task HostHealthSaysThereAreNoHostsBeforeTheUserRunsACheck()
    {
        var page = await fixture.NewPageAsync("/?nohosts");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-health").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-health-no-hosts")).ToBeVisibleAsync();

        await page.GetByTestId("host-health-add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("empty-state")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("add-first-host")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Empty-by-filter is not empty-by-default: the vault still has hosts, so
    /// the way out is clearing the search, not adding a host.
    /// </summary>
    [Fact]
    public async Task AnEmptyHostSearchResultOffersToClearTheSearch()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("search").ClickAsync();
        await page.GetByTestId("host-search").FillAsync("zzzz-no-such-host");
        await Assertions.Expect(page.GetByTestId("host-search-empty")).ToBeVisibleAsync();

        await page.GetByTestId("clear-host-search").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-search-empty")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-row").First).ToBeVisibleAsync();
    }
}
