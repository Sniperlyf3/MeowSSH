using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The rules that hold on every screen, swept rather than sampled.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PhoneWidthConsistencyTests"/> already asserts a 44px floor, but only
/// over primary navigation, icon buttons, and the credential form. That is why an
/// app-wide screenshot audit found 13x13 checkboxes rendering in the platform's
/// blue, a 16px-tall slider, 23px disclosure rows and 25px breadcrumbs — none of
/// them were in the sample.
/// </para>
/// <para>
/// These tests sweep <em>every</em> visible interactive element on <em>every</em>
/// reachable screen instead. A control added tomorrow is covered without anyone
/// remembering to add it to a list.
/// </para>
/// </remarks>
[Collection(nameof(TestHostCollection))]
public class ControlSurfaceTests(TestHostFixture fixture)
{
    /// <summary>
    /// Every screen, and how to get there from a fresh load. Query-string
    /// scenarios first, then the ones behind a tap.
    /// </summary>
    private static readonly (string Name, string Url, string[] Taps)[] Screens =
    [
        ("setup", "/?setup", []),
        ("locked", "/?locked", []),
        ("hosts", "/", []),
        ("host-editor", "/?newhost", []),
        ("keys", "/?keys", []),
        ("files-browser", "/?files", []),
        ("diagnostic", "/?diagnostic", []),
        ("files-landing", "/", ["tab-files"]),
        ("tools-hub", "/", ["tab-tools"]),
        ("tool-tailcat", "/", ["tab-tools", "open-tool-tailcat"]),
        ("tool-actions", "/", ["tab-tools", "open-tool-actions"]),
        ("tool-monitoring", "/", ["tab-tools", "open-tool-monitoring"]),
        ("tool-health", "/", ["tab-tools", "open-tool-health"]),
        ("tool-backup", "/", ["tab-tools", "open-tool-backup"]),
        ("settings", "/", ["tab-settings"]),
        ("settings-appearance", "/", ["tab-settings", "open-appearance-settings"]),
        ("settings-plan", "/", ["tab-settings", "open-plan-settings"]),
        ("settings-session-logs", "/", ["tab-settings", "open-session-log-settings"]),
        ("settings-privacy", "/", ["tab-settings", "open-privacy-diagnostics"]),
        ("settings-about", "/", ["tab-settings", "open-about-settings"]),
    ];

    private const string Interactive =
        "button:visible, a[href]:visible, input:visible, select:visible, textarea:visible, " +
        "[role=button]:visible, [role=tab]:visible, summary:visible";

    private async Task<IPage> OpenAsync(string url, string[] taps)
    {
        var page = await fixture.NewPageAsync(url);
        await page.SetViewportSizeAsync(412, 915);
        foreach (var tap in taps)
        {
            await page.GetByTestId(tap).First.ClickAsync();
            await page.WaitForTimeoutAsync(200);
        }
        return page;
    }

    [Fact]
    public async Task EveryInteractiveElementMeetsTheTouchFloor()
    {
        var failures = new List<string>();

        foreach (var (name, url, taps) in Screens)
        {
            var page = await OpenAsync(url, taps);
            var undersized = await page.Locator(Interactive).EvaluateAllAsync<string[]>(
                """
                els => els.filter(el => {
                    const r = el.getBoundingClientRect();
                    return r.width > 0 && r.height > 0 && (r.width < 44 || r.height < 44);
                }).map(el => {
                    const r = el.getBoundingClientRect();
                    const id = el.getAttribute('data-testid') || el.getAttribute('aria-label')
                        || (el.textContent || '').trim().slice(0, 24) || el.tagName;
                    return `${id} ${Math.round(r.width)}x${Math.round(r.height)}`;
                })
                """);

            if (undersized.Length > 0) failures.Add($"{name}: {string.Join(", ", undersized)}");
        }

        Assert.True(failures.Count == 0,
            "Interactive elements below 44x44 CSS pixels:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task NoControlIsLeftInThePlatformsOwnStyling()
    {
        // appearance:auto means the browser draws it, which on Android means a
        // 13px box in Chrome's blue -- in an app whose accent is apricot.
        var failures = new List<string>();

        foreach (var (name, url, taps) in Screens)
        {
            var page = await OpenAsync(url, taps);
            var native = await page
                .Locator("input[type=checkbox]:visible, input[type=radio]:visible, input[type=range]:visible, select:visible")
                .EvaluateAllAsync<string[]>(
                    """
                    els => els.filter(el => getComputedStyle(el).appearance === 'auto')
                              .map(el => `${el.tagName}:${el.type || ''}`)
                    """);

            if (native.Length > 0) failures.Add($"{name}: {string.Join(", ", native.Distinct())}");
        }

        Assert.True(failures.Count == 0,
            "Controls still drawn by the platform:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task EveryScreenHasExactlyOneTabBar()
    {
        // AppShell renders the tool pages, so a tool page that wrapped itself in
        // an AppShell produced two nested shells: two tab bars, and an outer
        // shell whose title and active tool never changed again.
        var failures = new List<string>();

        foreach (var (name, url, taps) in Screens)
        {
            var page = await OpenAsync(url, taps);
            var bars = await page.Locator("nav.tabbar").CountAsync();

            // The setup, lock, host-editor and file-browser screens are
            // deliberately shell-less: they are modal to the task at hand.
            if (bars > 1) failures.Add($"{name}: {bars} tab bars");
        }

        Assert.True(failures.Count == 0,
            "Nested app shells:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task NoScreenOffersTheSameLabelOnTwoVisibleControls()
    {
        // Two controls reading the same thing on one screen is the user being
        // asked a question they have no way to answer.
        var failures = new List<string>();

        foreach (var (name, url, taps) in Screens)
        {
            var page = await OpenAsync(url, taps);
            var duplicates = await page.Locator("button:visible, a[href]:visible, [role=button]:visible")
                .EvaluateAllAsync<string[]>(
                    """
                    els => {
                        const seen = {};
                        for (const el of els) {
                            const label = (el.getAttribute('aria-label') || el.textContent || '')
                                .replace(/\s+/g, ' ').trim();
                            if (!label) continue;
                            (seen[label] = seen[label] || []).push(el);
                        }
                        return Object.entries(seen)
                            .filter(([, v]) => v.length > 1)
                            .map(([k, v]) => `"${k}" x${v.length}`);
                    }
                    """);

            if (duplicates.Length > 0) failures.Add($"{name}: {string.Join(", ", duplicates)}");
        }

        Assert.True(failures.Count == 0,
            "Duplicate visible control labels:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task NoTextIsSqueezedIntoAColumnTooNarrowToRead()
    {
        // The Settings cards put a one-line description into a 55px column and
        // wrapped it six times, because a long status value beside it was
        // flex:none and took the row.
        var failures = new List<string>();

        foreach (var (name, url, taps) in Screens)
        {
            var page = await OpenAsync(url, taps);
            var squeezed = await page.Locator(".setting-card__copy:visible").EvaluateAllAsync<string[]>(
                """
                els => els.filter(el => el.getBoundingClientRect().width < 140)
                          .map(el => `${(el.textContent || '').trim().slice(0, 28)} ${Math.round(el.getBoundingClientRect().width)}px`)
                """);

            if (squeezed.Length > 0) failures.Add($"{name}: {string.Join(", ", squeezed)}");
        }

        Assert.True(failures.Count == 0,
            "Card text squeezed below 140px:\n" + string.Join("\n", failures));
    }
}
