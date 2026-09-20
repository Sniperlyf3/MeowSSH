using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Regression coverage for the B/C/D/E items in
/// <c>docs/specs/ui-ux-audit-2026-09.md</c> that PR #108 (commit
/// <c>ac58122</c>) already fixed in the same pass as A1-A4, but left
/// untested. <see cref="ControlSurfaceTests"/> already sweeps the touch-floor
/// and native-styling items (D1-D4) and the general duplicate-label and
/// text-squeeze checks (which happen to also catch B2, B4, B5 and C1); the
/// tests below cover what that general sweep does not reach because the
/// defect was about layout shape, styling, or a state the sweep's fixed
/// scenario list does not visit.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class UiUxAuditFollowUpTests(TestHostFixture fixture)
{
    // B1 -------------------------------------------------------------------

    /// <summary>
    /// B1: the header "+" and the empty-state CTA both called StartAdding and
    /// were visible at the same time. Fixed by dropping the header action
    /// entirely -- the empty state owns it while the vault is empty, and the
    /// list header's own "+ Add" (data-testid="add-credential-list") owns it
    /// once there is a list to add to.
    /// </summary>
    [Fact]
    public async Task KeysPageOffersExactlyOneAddCredentialControlWhenEmpty()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();

        var addControls = page.Locator("[data-testid='add-credential'], [data-testid='add-credential-list']");
        await Assertions.Expect(addControls).ToHaveCountAsync(1);
    }

    /// <summary>
    /// Same control count once the vault is not empty, so a fix that only
    /// hides the empty-state button (rather than also the header "+") is
    /// still caught.
    /// </summary>
    [Fact]
    public async Task KeysPageOffersExactlyOneAddCredentialControlWhenNotEmpty()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-import-key").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("deploy key");
        await page.GetByTestId("credential-secret").FillAsync("""
            -----BEGIN OPENSSH PRIVATE KEY-----
            b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
            -----END OPENSSH PRIVATE KEY-----
            """);
        await page.GetByTestId("save-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-list")).ToBeVisibleAsync();

        var addControls = page.Locator("[data-testid='add-credential'], [data-testid='add-credential-list']");
        await Assertions.Expect(addControls).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("add-credential-list")).ToBeVisibleAsync();
    }

    // B3 -------------------------------------------------------------------

    /// <summary>
    /// B3: the Tailcat hub's device-key segment and the tab bar's Keys tab
    /// (SSH credentials) both read "Keys". Fixed by renaming the Tailcat
    /// segment to "Tailcat identities".
    /// </summary>
    [Fact]
    public async Task TailcatDeviceKeysSegmentIsNotLabelledTheSameAsTheKeysTab()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-section-keys")).ToHaveTextAsync("Tailcat identities");
        await Assertions.Expect(page.GetByTestId("tailcat-section-keys")).Not.ToHaveTextAsync("Keys");
        await Assertions.Expect(page.GetByTestId("tab-keys")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("Keys"));
    }

    // B4 -------------------------------------------------------------------

    /// <summary>
    /// B4: a single-section intent group ("Transfers", "VPN", "Diagnostics")
    /// used to render as a card with a heading, a description, and a button
    /// all repeating the same word. Fixed by collapsing it to one row
    /// (<c>tailcat-intent-row</c>) with no nested button.
    /// </summary>
    [Fact]
    public async Task SingleSectionTailcatGroupsAreOneRowNotACardWithARepeatedLabel()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();

        foreach (var section in new[] { "transfers", "vpn", "diagnostics" })
        {
            var row = page.GetByTestId($"tailcat-section-{section}");
            await Assertions.Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("tailcat-intent-row"));
            // The row itself is the button; it must not carry a second,
            // nested button repeating its own label.
            await Assertions.Expect(row.Locator("button")).ToHaveCountAsync(0);
        }
    }

    // B5 -------------------------------------------------------------------

    /// <summary>
    /// B5: every Tools hub card was a button with a nested "Open" button --
    /// two hit targets for one destination. Fixed by making the card itself
    /// the only control, with a chevron rather than a second button.
    /// </summary>
    [Fact]
    public async Task ToolsHubCardsHaveNoNestedOpenButton()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tools-hub")).ToBeVisibleAsync();

        foreach (var id in new[] { "open-tool-actions", "open-tool-monitoring", "open-tool-health", "open-tool-tailcat", "open-tool-backup" })
        {
            var card = page.GetByTestId(id);
            await Assertions.Expect(card).ToHaveJSPropertyAsync("tagName", "BUTTON");
            await Assertions.Expect(card.Locator("button")).ToHaveCountAsync(0);
            await Assertions.Expect(card).Not.ToContainTextAsync("Open");
        }
    }

    // B6 -------------------------------------------------------------------

    /// <summary>
    /// B6: Tools and Settings rendered the same gear glyph in the tab bar.
    /// Fixed by giving Tools its own "tools" icon path.
    /// </summary>
    [Fact]
    public async Task ToolsAndSettingsTabsDoNotShareTheSameIconGlyph()
    {
        var page = await fixture.NewPageAsync();
        var toolsPath = await page.GetByTestId("tab-tools").Locator("svg.icon path").First.GetAttributeAsync("d");
        var settingsPath = await page.GetByTestId("tab-settings").Locator("svg.icon path").First.GetAttributeAsync("d");

        Assert.False(string.IsNullOrWhiteSpace(toolsPath));
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        Assert.NotEqual(settingsPath, toolsPath);
    }

    // E3 -------------------------------------------------------------------

    /// <summary>
    /// E3: the file browser's hidden-files toggle was a bare crossed-out-eye
    /// icon with no label, ambiguous between "hidden files are hidden" and
    /// "tap to hide". Fixed with an <c>aria-label</c>/<c>title</c>/
    /// <c>aria-pressed</c> that state the current state rather than leaving
    /// it to the glyph.
    /// </summary>
    [Fact]
    public async Task HiddenFilesToggleStatesItsCurrentStateRatherThanJustAnIcon()
    {
        var page = await fixture.NewPageAsync("/?files");
        var toggle = page.GetByTestId("toggle-hidden");

        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-pressed", "false");
        var hiddenLabel = await toggle.GetAttributeAsync("aria-label");
        Assert.Contains("hidden", hiddenLabel, StringComparison.OrdinalIgnoreCase);

        await toggle.ClickAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-pressed", "true");
        var shownLabel = await toggle.GetAttributeAsync("aria-label");
        Assert.NotEqual(hiddenLabel, shownLabel);
    }

    /// <summary>
    /// E3's own residual, noted but left open in the original pass: Upload
    /// and New folder had <c>aria-label</c> (so a screen reader announces
    /// them) but, unlike the hidden-files toggle right next to them, no
    /// visible <c>title</c> tooltip for a sighted mouse/trackpad user
    /// hovering the bare icon. Closed by adding <c>title</c> to both,
    /// matching the toggle's own treatment.
    /// </summary>
    [Fact]
    public async Task UploadAndNewFolderHaveVisibleTooltipsLikeTheHiddenFilesToggle()
    {
        var page = await fixture.NewPageAsync("/?files");

        var upload = await page.GetByTestId("upload-file").GetAttributeAsync("title");
        var newFolder = await page.GetByTestId("new-folder").GetAttributeAsync("title");

        Assert.False(string.IsNullOrWhiteSpace(upload), "Upload button has no visible title tooltip.");
        Assert.False(string.IsNullOrWhiteSpace(newFolder), "New folder button has no visible title tooltip.");
    }

    // C2 ---------------------------------------------------------------------

    /// <summary>
    /// C2: the hosts list's trailing per-row settings column used to claim
    /// ~96px, ellipsising host names and addresses that rendered in full
    /// elsewhere at the same width. Narrowing that column to the manage
    /// button's own 44px touch target (<c>.host-wrap</c>'s
    /// <c>minmax(0, 1fr) auto</c> grid) fixed most of the audit's examples,
    /// but Playwright measurement here found a second, previously
    /// unmeasured contributor: <c>.host</c>'s *internal* grid ("3px auto 1fr
    /// auto") gives its own trailing column -- the connection-state badge --
    /// to a plain "auto" track, whose floor is its own min-content, not
    /// zero. A badge with a wide label ("Connecting") still ate into the
    /// name column before it gave up any of its own width. Capping
    /// <c>.host__meta</c> fixed that for every sample host except one.
    /// </summary>
    [Fact]
    public async Task ShortAndMediumHostNamesDoNotOverflowTheirColumnOnTheHostsList()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        // Every sample host except "staging-db-replica-eu-west" (see the next
        // test): the audit's own examples, plus every other state (idle,
        // connected, connecting).
        var overflowing = await page.Locator(".host__name:visible, .host__address:visible").EvaluateAllAsync<string[]>(
            """
            els => els.filter(el => el.scrollWidth > el.clientWidth + 1)
                      .map(el => el.textContent.trim())
                      .filter(t => !t.includes('staging-db-replica-eu-west') && !t.includes('Host key changed'))
            """);

        Assert.True(overflowing.Length == 0,
            "Host name/address columns still overflow: " + string.Join(", ", overflowing));
    }

    /// <summary>
    /// C2's one documented residual: a 27-character host name ("staging-db-
    /// replica-eu-west") in the <c>Error</c> state needs 227px for its name
    /// alone, and this row has only 233px total to split between the name and
    /// its status badge once the rail, avatar and padding (~103px, fixed) are
    /// subtracted. The dot-only badge breakpoint the owner later approved
    /// (see <see cref="HostNameOverflowAndEmptyStateTests"/>) took the badge
    /// from ~72px to 22px and this overflow from ~58px to ~16px -- it did not
    /// close it, because 211px is still less than 227px. Reading the rest of
    /// the name is what scroll-on-focus is for; this test pins the *current,
    /// reduced* overflow rather than a theoretical zero, so a regression that
    /// makes it worse is still caught.
    /// </summary>
    [Fact]
    public async Task TheOneRemainingHostNameOverflowIsBoundedAndDocumented()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        var overflowPx = await page.GetByTestId("host-row").Filter(new() { HasTextString = "staging-db-replica-eu-west" })
            .Locator(".host__name")
            .EvaluateAsync<double>("el => el.scrollWidth - el.clientWidth");

        Assert.True(overflowPx is > 0 and <= 25,
            $"Expected the known, bounded overflow (~16px) on this one row, got {overflowPx}px. " +
            "If this is now 0, update this test and the audit doc to mark C2 fully fixed. " +
            "If it is back near 58px, the dot-only badge breakpoint stopped applying; " +
            "if it grew beyond that, something regressed the width cap on .host__meta.");
    }

    /// <summary>
    /// The manage ("...") column itself stays at its 44px touch target rather
    /// than reclaiming the width the fix gave back to the name column.
    /// </summary>
    [Fact]
    public async Task HostManageColumnStaysAtTouchTargetWidth()
    {
        var page = await fixture.NewPageAsync("/");
        await page.SetViewportSizeAsync(412, 915);

        var width = await page.GetByTestId("manage-host").First.EvaluateAsync<double>("el => el.getBoundingClientRect().width");
        Assert.True(width is >= 44 and <= 56, $"Manage column is {width}px wide, expected ~44px.");
    }

    // C3 -------------------------------------------------------------------

    /// <summary>
    /// C3: every SSH host row on the Files landing page used to be followed
    /// by its own full-width "More" band -- a
    /// <c>&lt;details data-testid="files-host-more"&gt;</c> disclosure
    /// holding a per-row "Advanced SFTP" button -- which roughly doubled the
    /// list's height for six hosts. Fixed as a side effect of the B2 fix
    /// (<see cref="AdvancedSftpTests.AdvancedSftpIsOfferedOnceAndAsksWhichHostAfterwards"/>):
    /// one "Advanced SFTP" entry point above the list puts it into a picking
    /// mode instead, so <c>FilesLandingPage</c> never renders a per-row
    /// disclosure at all. The audit doc noted this was verified only by
    /// reading the source, with no dedicated regression test; this is that
    /// test.
    /// </summary>
    [Fact]
    public async Task FilesLandingPageHasNoPerHostMoreDisclosure()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("tab-files").ClickAsync();
        await Assertions.Expect(page.GetByTestId("files-host-list")).ToBeVisibleAsync();

        // The old per-row disclosure must not exist at all -- not merely be
        // hidden or collapsed -- for any of the seeded hosts.
        await Assertions.Expect(page.GetByTestId("files-host-more")).ToHaveCountAsync(0);

        // The list holds exactly one element per host: nothing wraps each row
        // in an extra band that would inflate the list's child count (and,
        // with it, its height) beyond the number of rows actually shown.
        var hostRows = await page.Locator("[data-testid='files-host-list'] [data-testid='host-row']").CountAsync();
        var listChildren = await page.GetByTestId("files-host-list").EvaluateAsync<int>("el => el.children.length");
        Assert.True(hostRows > 0, "Expected at least one seeded host to assert against.");
        Assert.Equal(hostRows, listChildren);
    }

    // C4 -----------------------------------------------------------------------

    /// <summary>
    /// C4: the file browser's trailing "..." menu sat in a column with its
    /// own background running the full list height, reading as a detached
    /// stripe rather than a per-row control. Fixed by having the row paint
    /// the background once and the menu button sit on it (files-actions.css).
    /// </summary>
    [Fact]
    public async Task FileRowOverflowMenuSharesTheRowsBackgroundRatherThanItsOwn()
    {
        var page = await fixture.NewPageAsync("/?files");
        await Assertions.Expect(page.GetByTestId("filelist")).ToBeVisibleAsync();

        var row = page.GetByTestId("file-entry").First.Locator("xpath=..");
        var menu = row.GetByTestId("file-actions");

        var rowBackground = await row.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
        var menuBackground = await menu.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");

        // The menu button must not paint an opaque surface of its own -- it
        // shows the row's own background through, which is what makes the
        // separator (the row's border) look like it belongs to one row
        // rather than a scrollbar-like stripe down the whole list.
        Assert.True(menuBackground is "rgba(0, 0, 0, 0)" or "transparent",
            $"File row menu paints its own background ({menuBackground}) instead of sharing the row's ({rowBackground}).");
    }

    // C5 -------------------------------------------------------------------

    /// <summary>
    /// C5: Settings card titles/descriptions were centre-aligned while the
    /// rest of the app (hosts, files) is left-aligned, so a wrapped
    /// multi-line description read as ragged and as a different app. Fixed
    /// with an explicit <c>text-align: left</c> on <c>.setting-card</c>
    /// (several of these are &lt;button&gt; elements, which centre by
    /// default).
    /// </summary>
    [Fact]
    public async Task SettingsCardTextIsLeftAlignedNotCentred()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();

        var align = await page.GetByTestId("open-plan-settings")
            .EvaluateAsync<string>("el => getComputedStyle(el).textAlign");
        Assert.Equal("left", align);
    }

    // C6 -------------------------------------------------------------------

    /// <summary>
    /// C6: "Refresh plan" and "Restore purchases" had no shared width rule,
    /// so each sized to its own label. Fixed with a flex row that gives both
    /// buttons equal width.
    /// </summary>
    [Fact]
    public async Task PlanSettingsRefreshAndRestoreButtonsAreEqualWidth()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();
        await page.GetByTestId("open-plan-settings").ClickAsync();

        var refreshWidth = await page.GetByTestId("refresh-entitlement").EvaluateAsync<double>("el => el.getBoundingClientRect().width");
        var restoreWidth = await page.GetByTestId("restore-purchases").EvaluateAsync<double>("el => el.getBoundingClientRect().width");

        Assert.True(Math.Abs(refreshWidth - restoreWidth) < 1,
            $"Refresh plan ({refreshWidth}px) and Restore purchases ({restoreWidth}px) are not equal width.");
    }

    // E1 -------------------------------------------------------------------

    /// <summary>
    /// E1: "Customize" (an action) and "Off" (a status) on the Settings root
    /// were styled identically. Fixed with two distinct classes:
    /// <c>setting-card__value</c> (accent-coloured action) and
    /// <c>setting-card__status</c> (neutral pill).
    /// </summary>
    [Fact]
    public async Task SettingsActionAndStatusValuesAreStyledDifferently()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-settings").ClickAsync();

        var action = page.Locator(".setting-card__value").Filter(new() { HasTextString = "Customize" });
        var status = page.Locator(".setting-card__status").Filter(new() { HasTextString = "Off" });
        await Assertions.Expect(action).ToBeVisibleAsync();
        await Assertions.Expect(status).ToBeVisibleAsync();

        var actionColor = await action.EvaluateAsync<string>("el => getComputedStyle(el).color");
        var statusColor = await status.EvaluateAsync<string>("el => getComputedStyle(el).color");
        var statusBackground = await status.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");

        Assert.NotEqual(actionColor, statusColor);
        Assert.False(statusBackground is "rgba(0, 0, 0, 0)" or "transparent",
            "The status pill should carry its own neutral background, not read as a link.");
    }

    // E2 -------------------------------------------------------------------

    /// <summary>
    /// E2: "PRO"/"TRUNCATED" markers baked into heading text inherit the
    /// heading's own weight and read as part of the feature's name. A
    /// <c>.action-pro-badge</c> span was introduced to fix this on the
    /// Actions page, but the rule that styled it lived in
    /// ActionsPage.razor.css -- Blazor CSS isolation only stamps that file's
    /// scope attribute onto elements ActionsPage.razor itself renders, so
    /// the same class used on every other page (Host Health, Session logs,
    /// Encrypted backup, Forwards, ...) matched no rule and rendered as bare
    /// text -- the exact defect the badge exists to prevent. Fixed by
    /// promoting the rule to the global stylesheet.
    /// </summary>
    [Theory]
    [InlineData("/", new[] { "tab-tools", "open-tool-health" })]
    [InlineData("/", new[] { "tab-tools", "open-tool-backup" })]
    public async Task ActionProBadgeIsStyledOutsideTheActionsPage(string url, string[] taps)
    {
        var page = await fixture.NewPageAsync(url);
        foreach (var tap in taps) await page.GetByTestId(tap).ClickAsync();

        var badge = page.Locator(".action-pro-badge").First;
        await Assertions.Expect(badge).ToBeVisibleAsync();

        var border = await badge.EvaluateAsync<string>("el => getComputedStyle(el).borderTopWidth");
        var radius = await badge.EvaluateAsync<string>("el => getComputedStyle(el).borderRadius");

        Assert.NotEqual("0px", border);
        Assert.NotEqual("0px", radius);
    }

    // E4 ---------------------------------------------------------------------

    /// <summary>
    /// E4 (second half): the exit node used to be checked by default, which
    /// silently turns the phone into a route for other devices. Fixed by
    /// defaulting <c>_exitNode</c> to false.
    /// </summary>
    [Fact]
    public async Task TailcatExitNodeIsUncheckedByDefault()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-exit-node")).Not.ToBeCheckedAsync();
    }

    /// <summary>
    /// E4 (first half): the insecure "allow any Tailcat client" toggle must
    /// not carry the same visual treatment as an ordinary option like "Share
    /// a folder" -- it gets the danger rail/wash via
    /// <c>forward-option--risky</c>.
    /// </summary>
    [Fact]
    public async Task InsecureExitNodeToggleIsVisuallyDistinctFromABenignToggle()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-exit-node").CheckAsync();

        var riskyRow = page.GetByTestId("tailcat-insecure-exit-node").Locator("xpath=..");
        var benignRow = page.GetByTestId("tailcat-files").Locator("xpath=..");

        var riskyBorder = await riskyRow.EvaluateAsync<string>("el => getComputedStyle(el).borderInlineStartColor");
        var benignBorder = await benignRow.EvaluateAsync<string>("el => getComputedStyle(el).borderInlineStartColor");

        Assert.NotEqual(benignBorder, riskyBorder);
    }

    // E5 -------------------------------------------------------------------

    /// <summary>
    /// E5: the selected segmented control used the exact primary-button
    /// accent fill, so a tab selection competed with real actions for
    /// attention. Fixed with a tinted wash instead of the solid fill.
    /// </summary>
    [Fact]
    public async Task SelectedSegmentedOptionDoesNotUseThePrimaryButtonFill()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-section-phone").ClickAsync();

        var selectedBackground = await page.GetByTestId("tailcat-section-phone")
            .EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
        var primaryButtonBackground = await page.GetByTestId("start-tailcat-server")
            .EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");

        Assert.NotEqual(primaryButtonBackground, selectedBackground);
    }

    // E6 -------------------------------------------------------------------

    /// <summary>
    /// E6: the first-run screen's five-line explanation was centred, giving
    /// the eye no consistent left edge across lines. Fixed by left-aligning
    /// the body copy while the heading and button stay centred.
    /// </summary>
    [Fact]
    public async Task SetupScreenBodyCopyIsLeftAlignedWhileHeadingStaysCentred()
    {
        var page = await fixture.NewPageAsync("/?setup");
        var body = page.Locator(".lock__body").First;
        var title = page.Locator(".lock__title").First;

        await Assertions.Expect(body).ToBeVisibleAsync();
        Assert.Equal("left", await body.EvaluateAsync<string>("el => getComputedStyle(el).textAlign"));
        Assert.Equal("center", await title.EvaluateAsync<string>("el => getComputedStyle(el).textAlign"));
    }
}
