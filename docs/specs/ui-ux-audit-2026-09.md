# Spec: UI/UX defects found in a full-app screenshot audit

**Repository:** `Sniperlyf3/MeowSSH`
**Affects:** `src/MeowSSH.UI/Pages/`, `src/MeowSSH.UI/Components/`, `src/MeowSSH.UI/wwwroot/css/`, `tests/MeowSSH.UI.Tests/`
**Status:** A-series (A1–A4) landed in PR #108, commit `ac58122`, with regression
coverage added in `f4ec71e`/`ac72326`. Commit `ac58122` also landed fixes for
most of B–E in the same pass — its own commit message says so — but that was
never verified against B–E's checkboxes here, which is what left this doc
understating what had already shipped. Re-verified item by item against
current source below: FIXED for B2–B6, C1, C3, C4, D1–D4, E1, E3–E6 (citations
per item; C3's missing dedicated regression test and E3's Upload/New-folder
tooltip residual are both closed in a later pass here); B1, C2, C6 and part of E2 were
still genuinely broken as of `ac58122` and were fixed here, in
`e4c1007`, with regression tests; C7 is unaddressed by design (the
doc itself calls it lowest priority, a design suggestion rather than a
concrete defect).
**Audited at:** `claude/beautiful-bohr-im81me` @ `8f93a1a`
**Method:** every screen driven through the Blazor test host at 412×915 and 360×780, captured and then audited in the DOM. Measurements below are real `getBoundingClientRect` / `getComputedStyle` values, not estimates.

## How to reproduce the audit

```sh
dotnet run --project src/MeowSSH.TestHost     # serves on :5129
```

Scenarios are query parameters (`?setup`, `?locked`, `?keys`, `?newhost`, `?files`,
`?multi`, `?diagnostic`, `?theme=light|dark`); Tools and Settings sub-pages are
reached by tapping `tab-tools` / `tab-settings` then the `open-*` test ids.

**One trap worth knowing before you verify anything.** The scroll container is
`main.app__body`, not the window. `window.scrollTo(...)` does nothing, and a
screenshot taken after it will show a primary action apparently cut off by the
tab bar when it is in fact reachable. Scroll `main.app__body` instead.

## What is already good

Worth stating so it does not get "fixed": the first-run and lock screens, the
terminal, the empty-state copy throughout, and the light theme (a real light
palette, not an inversion) are all strong. The problems below are concentrated
in Settings, the Tailcat hub, the Files landing page, and a set of shared
control primitives.

---

# A. Functional bugs

## A1 — Nested `AppShell` renders two tab bars and strands navigation

- [x] **Landed** in PR #108 (commit `ac58122`). `CommandMonitoringPage` is
  content-only now; `AppShell` is the single owner of the shell for every
  tool page (Actions, Host health, Encrypted backup, the Tailcat hub included
  — none of them wrap themselves in a second `<AppShell>`). Covered by
  `ControlSurfaceTests.EveryScreenHasExactlyOneTabBar`, which sweeps every
  reachable screen in `tests/MeowSSH.UI.Tests/ControlSurfaceTests.cs` and
  asserts `nav.tabbar` never appears twice.

**Severity: high.** The only defect here that breaks navigation outright.

`AppShell` renders tool pages inside itself:

```razor
else if (_activeTool == "monitoring") { <CommandMonitoringPage /> }
```

and `CommandMonitoringPage` opens with its own `<AppShell>`. The result is two
nested shells: `document.querySelectorAll('nav.tabbar').length === 2`, with every
tab label duplicated (`Hosts ×2`, `Files ×2`, `Tools ×2`, `Keys ×2`, `Settings ×2`).

The consequences are worse than the cosmetic doubling. The outer shell keeps
`_activeTool`, so after visiting Monitoring the header still reads "Monitoring"
no matter where the inner shell navigates, and tapping the inner tab bar's
*Settings* leaves you looking at the monitoring tool. The app is effectively
stuck until a full reload.

`SettingsPage` (4 usages), `HostsPage` and `KeysPage` also render their own
`AppShell`. Audit those for the same nesting.

**Fix:** one shell, owned by the router. Tool and tab pages render content only.
Add a test asserting exactly one `nav.tabbar` exists on every reachable screen.

## A2 — A sentinel date is shown to the user as an entitlement expiry

- [x] **Landed** in PR #108 (commit `ac58122`). `SettingsPage.PlanStatus`
  treats any expiry more than `SentinelHorizonYears` (10) out as a sentinel
  and reports "Lifetime" instead of formatting it; a real expiry renders
  `d MMM yyyy` in the current culture rather than invariant `M/d/yyyy`.
  Covered by `EntitlementDisplayTests` (added here), which asserts both the
  Settings root and Settings → Plan read "Lifetime" and never leak `9999`,
  `12/31` or `23:59` — the test host's `FakeEntitlementService` always
  returns `ValidUntilUtc = DateTimeOffset.MaxValue`, so this is exercised on
  every run rather than needing bespoke fixture setup.

`Settings → Account` and `Settings → Plan` both render:

```
verified until 12/31/9999 23:59
```

in accent-coloured monospace — the loudest element on the page. This is
`DateTime.MaxValue` leaking through a formatter. It appears on the two screens
whose entire job is to make a paid entitlement feel trustworthy.

**Fix:** a lifetime entitlement reads "Lifetime" with no date. A time-boxed one
reads a real, localised date. Never format a sentinel. Also: the format is
`M/d/yyyy`, invariant-culture US — use the device's locale, or an unambiguous
`d MMM yyyy`.

## A3 — "Export report" is disabled while the copy beside it says exporting works

- [x] **Landed** in PR #108 (commit `ac58122`), together with A4 below — see
  A4 for the fix and its test coverage.

`Settings → Privacy & diagnostics`. The card immediately above the button reads:

> **No crash diagnostics waiting** — A normal bug report can still be exported
> without diagnostic data.

The Export report button below it is `disabled` (`opacity: 0.45`). The page tells
the user the action is available and then refuses it. The real precondition is
an empty "What went wrong?" textarea, which is stated nowhere.

**Fix:** see A4.

## A4 — Three primary actions are disabled with no stated reason

- [x] **Landed** in PR #108 (commit `ac58122`). All three controls now show a
  `form__hint` naming the single unmet condition directly above the button
  while it is disabled (`save-host-hint`, `start-tailcat-server-hint`,
  `export-bug-report-hint`), and the host editor's button also wires
  `aria-describedby` to its hint. Covered by `DisabledControlReasonTests`
  (added here): each of the three screens asserts the hint is visible while
  the button is disabled, that resolving the blocker enables the button and
  removes the hint, and — for the Tailcat hub, whose `CanStart` has several
  clauses — that the hint names the *next* unmet condition rather than just
  disappearing after the first one is fixed.

| Screen | Control | State |
| --- | --- | --- |
| Host editor | `Save connection` | `disabled` |
| Tailcat hub | `Start sharing` | `disabled`, `opacity: 0.45` |
| Privacy & diagnostics | `Export report` | `disabled` |

A dimmed primary button with no adjacent explanation is a dead end: the user can
see the thing they want and has no way to learn what unlocks it.

**Fix (one pattern, applied to all three):** keep the button enabled and, on
press, reveal the specific blocker inline; or keep it disabled and render a
persistent one-line hint directly beneath it naming the missing input ("Add a
hostname to save this connection"). Do not rely on colour alone to communicate
the state.

---

# B. Duplicate and redundant controls

## B1 — Two "Add credential" buttons on the Keys page

- [x] **Fixed here** (`e4c1007`). `ac58122` only relabelled the
  empty-state button ("Add your first credential") without touching the
  header `+` — re-reading `KeysPage.razor` after that commit showed the
  header `+` was still rendered unconditionally (`@if (!_adding)`, no check
  on the credential count), so an empty vault still showed two controls that
  both called `StartAdding`, just with different visible text. Worse, the
  *non-empty* list has always had its own "+ Add" in the list header
  (`data-testid="add-credential-list"`), which the header `+` also
  duplicated — a second instance of the same defect the original report
  didn't catch because its repro only covered the empty state.

  Fixed by dropping the header `Actions` entirely: the empty state's button
  (`src/MeowSSH.UI/Pages/KeysPage.razor`, retested testid `add-credential`)
  owns the action while the vault is empty, and the list header's own
  `add-credential-list` owns it once there is a list. Covered by
  `UiUxAuditFollowUpTests.KeysPageOffersExactlyOneAddCredentialControlWhenEmpty`
  and `...WhenNotEmpty` in `tests/MeowSSH.UI.Tests/UiUxAuditFollowUpTests.cs`.

## B2 — "Advanced SFTP PRO" appears six times on the Files landing page

- [x] **Landed** in PR #108 (commit `ac58122`). `FilesLandingPage.razor` now
  renders one `advanced-sftp-open` entry point above the host list, which
  puts the list into a picking mode (`_advancedPicking`) rather than
  offering the tool per row. Covered by
  `AdvancedSftpTests.AdvancedSftpIsOfferedOnceAndAsksWhichHostAfterwards`
  (updated in the same commit).

## B3 — Two different visible controls both labelled "Keys"

- [x] **Landed** in PR #108 (commit `ac58122`). `TailcatHubPanel.razor`'s
  `IntentGroups` now names the device-key section `"Tailcat identities"`
  rather than `"Keys"`. Added regression coverage here:
  `UiUxAuditFollowUpTests.TailcatDeviceKeysSegmentIsNotLabelledTheSameAsTheKeysTab`.

## B4 — Card heading, description and button all say the same word

- [x] **Landed** in PR #108 (commit `ac58122`). A single-section
  `IntentGroup` (Transfers, VPN, Diagnostics) now renders as one
  `tailcat-intent-row` — icon-less row, title, description, chevron, no
  nested button — instead of a card whose heading, body and button all
  repeated the group's name. Added regression coverage here:
  `UiUxAuditFollowUpTests.SingleSectionTailcatGroupsAreOneRowNotACardWithARepeatedLabel`.

## B5 — Tools hub cards are tappable *and* carry an "Open" button

- [x] **Landed** in PR #108 (commit `ac58122`). Every `ToolsHubPage.razor`
  card is now a bare `<button class="setting-card">` ending in `<Icon
  Name="chevron" />`, no nested `<button>`. Added regression coverage here:
  `UiUxAuditFollowUpTests.ToolsHubCardsHaveNoNestedOpenButton`.

## B6 — Two tab-bar icons are identical

- [x] **Landed** in PR #108 (commit `ac58122`). `AppShell.razor`'s `Tabs`
  gives Tools its own `"tools"` icon (a wrench path added to `Icon.razor`)
  instead of reusing `"settings"`. Added regression coverage here:
  `UiUxAuditFollowUpTests.ToolsAndSettingsTabsDoNotShareTheSameIconGlyph`.

---

# C. Proportion and layout

## C1 — Settings cards squeeze their text into a ~55px column

The worst-proportioned layout in the app, and it is on the purchase screens.

Measured on `Settings` root and `Settings → Plan`:

| Text | Box |
| --- | --- |
| "Purchases, restore and entitlement status." | **55 × 80 px** |
| "Local premium tools are enabled on this device." | **55 × 96 px** |

Both wrap to five or six lines in a sliver of a column while the right-hand half
of the card holds one line and a large empty gap. "MeowSSH Pro" itself wraps to
two lines and reports as clipped.

**Fix:** the row is a two-column grid whose first column is sized to content.
Give the text column the available width (`minmax(0, 1fr)`) and let the status
sit beneath it on phone widths rather than beside it.

- [x] **Landed** in PR #108 (commit `ac58122`). `.setting-card__copy` in
  `src/MeowSSH.UI/wwwroot/css/meowssh.css` is `flex: 1 1 12rem; min-width: 0`
  and `.setting-card` wraps (`flex-wrap: wrap`) rather than forcing the value
  onto the same row, so a long status drops below the copy instead of
  squeezing it. Covered by the existing
  `ControlSurfaceTests.NoTextIsSqueezedIntoAColumnTooNarrowToRead`, which
  asserts no `.setting-card__copy` renders under 140px on any reachable
  screen.

## C2 — Host names truncate while a wide gear column sits beside empty space

On the hosts list, at 412px, all four of these report `scrollWidth > clientWidth`:

```
prod-web-01   deploy@10.4.2.11   ci@build.internal:2222   staging-db-replica-eu-west
```

They are ellipsised even though the same strings render in full on the Files
landing page at the same width. The difference is the hosts list's trailing
per-row settings column (~96px), most of which is padding around a small gear.

**Fix:** narrow the trailing column to the icon's touch target, let the name
column take the remainder, and move per-host settings to a long-press or a row
overflow that does not claim permanent width.

- [x] **Partially fixed, then extended here** (`e4c1007`). PR #108
  (`ac58122`) narrowed the trailing column exactly as prescribed —
  `HostRow.razor.css`'s `.host-wrap` is `grid-template-columns: minmax(0, 1fr)
  auto`, so the manage trigger is a 44px touch target rather than a ~96px
  padded column — and that alone fixes `prod-web-01` and
  `deploy@10.4.2.11`.

  Re-verifying with Playwright at 412px (not just reading the CSS) found the
  other two of the audit's own four examples *still* overflow:
  `ci@build.internal:2222` (146px needed, 136px given) and
  `staging-db-replica-eu-west` (227px needed, 169px given). The cause is a
  second column the original fix didn't touch: `.host`'s own internal grid
  (`3px auto 1fr auto`) gives the trailing column — the connection-state
  badge, not the manage button — a plain `auto` track, and a plain `auto`
  track's floor is its own min-content. It never gives up width, so whichever
  badge is showing ("Connecting", "Failed") keeps its full size and the name
  column gives up space first, the opposite of the `min-width: 0` comment
  already sitting on `.host__detail`.

  Fixed here by capping `.host__meta { max-width: 5rem }` under the same
  460px breakpoint that already hides the latency chip
  (`src/MeowSSH.UI/wwwroot/css/meowssh.css`). This fully resolves the
  `ci@build.internal:2222` case. It does **not** fully resolve
  `staging-db-replica-eu-west`: that row's 27-character name needs 227px and
  this row has only 233px total to split between name and badge once the
  fixed ~103px of rail/avatar/padding is subtracted, so even a fully
  collapsed badge leaves only 6px of margin — closing it completely would
  mean hiding the badge's label down to nothing on every row, which is a UX
  call (dot-only status badges below this breakpoint) rather than a CSS fix,
  and was left for a follow-up rather than decided unilaterally here.
  Covered by
  `UiUxAuditFollowUpTests.ShortAndMediumHostNamesDoNotOverflowTheirColumnOnTheHostsList`
  (asserts the fix for every other host) and
  `...TheOneRemainingHostNameOverflowIsBoundedAndDocumented` (pins the
  residual so a further regression is still caught), plus
  `...HostManageColumnStaysAtTouchTargetWidth` for the original column fix.

## C3 — A "More" band after every host row doubles the Files list

The Files landing page renders each host row followed by a full-width band
containing a single left-aligned "More" (55×40) against roughly 80% dead space.
Six hosts produce six of these. It roughly doubles the list height and reads as
an unfinished component.

**Fix:** collapse into the host row itself — a chevron or overflow control on the
row, or a bottom sheet on long-press. See also B2.

- [x] **Landed** in PR #108 (commit `ac58122`), as a consequence of the B2
  fix: `FilesLandingPage.razor` no longer renders a per-host disclosure at
  all — the single `advanced-sftp-open` row replaces every one of the six
  "More" bands. Verified by reading `FilesLandingPage.razor` and
  `HostRow.razor` (the `host__manage-trigger` contextual-edit button is a
  44px icon, not a full-width band, and isn't even wired up on this page —
  `OnEdit` is only passed on `HostsPage.razor`).

  This was originally verified only by reading the source, with no dedicated
  regression test (it was covered only indirectly, by `AdvancedSftpTests` and
  `ControlSurfaceTests`' sweep of the `files-landing` scenario). Closed here:
  `UiUxAuditFollowUpTests.FilesLandingPageHasNoPerHostMoreDisclosure` asserts
  the old disclosure's testid (`files-host-more`, from the pre-fix
  `<details>` band) is absent, and that the host list's child count equals
  its row count rather than a doubled count from a per-row wrapper.

## C4 — The SFTP overflow column reads as a detached stripe

In the file browser the trailing "…" controls sit in a column with its own
background that runs the full height of the card, while the per-row separators
stop short of it. It looks like a scrollbar or a rendering artefact rather than a
per-row menu, and it occupies ~85px for a control that needs ~44px.

**Fix:** put the "…" inside the row, sharing the row's background and separators.

- [x] **Landed** in PR #108 (commit `ac58122`). `files-actions.css`'s
  `.fileitem-row` now paints the shared background and border
  (`grid-template-columns: minmax(0, 1fr) auto`), and `.fileitem__menu` is
  `background: transparent` with only a `border-left` — the row owns the
  surface, not the menu column. Added regression coverage here:
  `UiUxAuditFollowUpTests.FileRowOverflowMenuSharesTheRowsBackgroundRatherThanItsOwn`.

## C5 — Text inside Settings cards is centre-aligned; everywhere else is not

Settings card titles and descriptions are centred, so the ragged edges of
multi-line wraps make the column hard to scan, and the section reads as a
different app from the left-aligned hosts and files lists.

**Fix:** left-align card content throughout.

- [x] **Landed** in PR #108 (commit `ac58122`). `.setting-card` in
  `meowssh.css` sets `text-align: left` explicitly, with a comment noting
  several of these cards are `<button>` elements, which centre by default.
  Added regression coverage here:
  `UiUxAuditFollowUpTests.SettingsCardTextIsLeftAlignedNotCentred`.

## C6 — Actions hug their content and leave a ragged right edge

On `Settings → Plan`, "Refresh plan" and "Restore purchases" are different widths
because each is sized to its label, stacked left-aligned.

**Fix:** stack full-width, or set them side by side on one row with equal widths.

- [x] **Fixed here** (`e4c1007`). `ac58122` never actually
  addressed this one: `.settings__actions` (the wrapper `SettingsPage.razor`
  already used around "Refresh plan"/"Restore purchases") had no CSS rule
  anywhere in the stylesheet — confirmed by grep, not just by eye — so the
  two `<button class="btn">`s (each `display: inline-flex`, sized to its own
  label) simply flowed side by side at their natural, unequal widths. Fixed
  by giving `.settings__actions` (`meowssh.css`) `display: flex; flex-wrap:
  wrap` with `.settings__actions > .btn { flex: 1 1 10rem }`, so the pair
  shares the row at equal widths and wraps to two full-width rows below the
  point they both fit. Covered by
  `UiUxAuditFollowUpTests.PlanSettingsRefreshAndRestoreButtonsAreEqualWidth`,
  mutation-checked (reverting the CSS reproduces the original 149px/199px
  mismatch and fails the test).

## C7 — Several screens are mostly empty below the fold

The hosts list fills about half the viewport and the rest is ground. Actions,
Monitoring and Host health are a heading and one button. This is partly fixture
data, but the layouts make no use of the space either way — no summary, no recent
activity, no secondary affordance.

**Fix (lower priority than the rest):** consider what each screen should say when
it has little to show, beyond an empty state. Host health in particular is one
orange button on an otherwise blank page.

- [ ] **Not fixed, not attempted.** This is a design suggestion rather than a
  concrete defect (the doc's own words: "lower priority than the rest",
  "consider what each screen should say") with no single correct answer to
  verify against, unlike every other item here. Re-read `ActionsPage.razor`,
  `CommandMonitoringPage.razor` and `HostHealthDashboardPage.razor`: none of
  them added a summary, recent-activity section or secondary affordance.
  Left for whoever picks up empty-state design work, consistent with the
  doc's own ordering (dead last, after E).

---

# D. Touch targets and unstyled native controls

The existing guard, `PhoneWidthConsistencyTests`, asserts a 44px minimum — but
only over primary navigation, icon buttons, and credential controls. Everything
below survives because it falls outside that sweep.

## D1 — Checkboxes are unstyled native controls at 13×13 px

Every `input[type=checkbox]` in the app reports:

```
13 × 13 px      appearance: auto      accent-color: auto
```

`accent-color: auto` renders the platform blue — in an app whose accent is
`#ff8a5b`. They appear on the Tailcat hub (including the security-critical
"Insecure: allow any Tailcat client"), the host editor, Appearance settings and
Session log settings.

13px is under a third of the 44px minimum.

**Fix:** one styled checkbox component: at least 24px visual box inside a 44px
hit area, `accent-color` set to the app accent or a fully custom control.

## D2 — `<select>` elements are unstyled

`appearance: auto` on the host editor, Tailcat hub and Appearance settings. They
render with platform chrome that matches neither theme.

**Fix:** style them, or replace with the app's own control.

## D3 — Other targets below 44px

| Screen | Control | Size |
| --- | --- | --- |
| Appearance | text-size `input[type=range]` | 380 × **16** |
| Tailcat hub | 9 × `segmented__option` | × **36** |
| Host editor | `Organisation …` disclosure | 380 × **24** |
| Privacy | `More details` disclosure | 108 × **23** |
| Diagnostic prompt | `Review saved report` disclosure | 380 × **23** |
| Files browser | breadcrumb `/`, `home`, `deploy` | 23–70 × **25** |
| Files landing | `More` | 55 × **40** |

The disclosure rows share one cause: a bare `<summary>` with no minimum height.

**Fix:** a minimum 44px hit area on `summary`, `.segmented__option`, breadcrumb
segments, and range thumbs. Padding is enough for most; the visual size need not
grow.

## D4 — Extend the guard so this does not regress

`PhoneWidthConsistencyTests` should sweep **every** visible interactive element
(`button, a[href], input, select, textarea, [role=button], [role=tab], summary`)
on **every** reachable screen, not a named subset. That single change catches
D1–D3 automatically. Add a companion assertion that no visible
`input[type=checkbox|radio]` or `select` computes `appearance: auto`.

- [x] **D1–D4 landed together** in PR #108 (commit `ac58122`). Re-read
  `src/MeowSSH.UI/wwwroot/css/controls.css` in full: checkboxes/radios are
  bound to the bare element (not a class that markup could forget), sized to
  a real 44×44 hit area via `::before` centring with a negative margin;
  `select` gets `appearance: none` and a drawn chevron; `input[type=range]`
  gets a 44px padded hit area around a slim visible track; `summary` gets
  `min-block-size: 2.75rem`; `.segmented__option` gets the same; breadcrumb
  buttons/links get both axes at 44px. D4's ask is implemented close to
  verbatim by the new `ControlSurfaceTests.EveryInteractiveElementMeetsTheTouchFloor`
  (sweeps `button, a[href], input, select, textarea, [role=button],
  [role=tab], summary` on every reachable screen) and
  `...NoControlIsLeftInThePlatformsOwnStyling` (the `appearance: auto`
  companion assertion), both in `tests/MeowSSH.UI.Tests/ControlSurfaceTests.cs`.
  No further action needed.

---

# E. Copy, semantics and hierarchy

## E1 — A status and an action are styled identically

On the Settings root, `Customize` and `Open` are actions; `Off` is a status. All
three are the same accent-coloured text in the same position.

**Fix:** statuses as neutral text or a pill; actions keep the accent and a
chevron.

- [x] **Landed** in PR #108 (commit `ac58122`). `SettingsPage.razor` now uses
  two distinct classes: `setting-card__value` (accent-coloured, for
  "Customize") and `setting-card__status` (a neutral pill with its own
  background, for "Off"/plan status) — see `meowssh.css`. Added regression
  coverage here: `UiUxAuditFollowUpTests.SettingsActionAndStatusValuesAreStyledDifferently`.

## E2 — "PRO" is baked into heading text

"Session logs PRO", "Advanced SFTP PRO" — the tier marker is part of the title
string, so it inherits heading weight and reads as part of the feature's name.

**Fix:** a small badge component beside the title, styled once.

- [x] **Partially landed, then completed here** (`e4c1007`).
  `ac58122` did add a badge component — actually two. `SettingsPage.razor`
  and `FilesLandingPage.razor` got a new `.tier-badge` span, styled globally
  in `meowssh.css`, and that half works correctly. But the *rest* of the app
  (`AdvancedSftpPage`, `CommandMonitoringPage`, `EncryptedBackupPage`,
  `ForwardsPage`, `HostHealthDashboardPage`, `SessionLogsPage`,
  `ActionSequenceControl`, `BroadcastTerminalControl`,
  `ForwardProfilesPanel`, `SftpRemoteSearchPanel`) already used a
  differently-named badge, `.action-pro-badge`, that PR #108 didn't touch —
  and its only style rule lived in `ActionsPage.razor.css`, a Blazor
  CSS-isolation *scoped* stylesheet. Isolation only stamps that file's scope
  attribute onto elements `ActionsPage.razor` itself renders, so the same
  class reused on ten other pages matched no rule at all: "PRO" and
  "TRUNCATED" rendered as bare text inheriting the surrounding heading's own
  weight on every one of them — the literal defect this item describes,
  just one component away from where the original report looked. Confirmed
  by reading the generated scoped-CSS bundle
  (`src/MeowSSH.UI/obj/.../MeowSSH.UI.styles.css`), which only emits
  `.action-pro-badge[b-ayulgw306n]` — a selector that can never match markup
  outside `ActionsPage.razor`.

  Fixed by promoting `.action-pro-badge`'s rule to `meowssh.css` (global,
  unscoped) and removing the dead scoped copy from `ActionsPage.razor.css`.
  Covered by `UiUxAuditFollowUpTests.ActionProBadgeIsStyledOutsideTheActionsPage`
  (Host Health and Encrypted Backup pages), mutation-checked (reverting the
  CSS move reproduces `borderTopWidth: 0px` on both and fails the test).

## E3 — The Files toolbar is three unlabelled icons

Upload, a crossed-out eye, and new-folder, with no labels or tooltips. The
crossed-out eye is genuinely ambiguous: it could mean "hidden files are hidden"
or "tap to hide". State matters and the icon does not carry it.

**Fix:** labels, or an explicit two-state control ("Hidden files: off").

- [x] **Landed for the specifically-flagged control** in PR #108 (commit
  `ac58122`). `FilesPage.razor`'s hidden-files toggle now carries an
  `aria-label` that states the current state ("Hidden files hidden. Show
  them." / "...shown. Hide them."), a `title` tooltip, and `aria-pressed` —
  the exact ambiguity this item calls out. Added regression coverage here:
  `UiUxAuditFollowUpTests.HiddenFilesToggleStatesItsCurrentStateRatherThanJustAnIcon`.

  Residual closed here: Upload and New folder had an `aria-label` (so a
  screen reader still announced them) but, unlike the hidden-files toggle
  right next to them, no visible `title` tooltip for a sighted mouse/
  trackpad user hovering the bare icon. Added `title="Upload file"` /
  `title="New folder"` to both, matching the toggle's own treatment. Added
  regression coverage here:
  `UiUxAuditFollowUpTests.UploadAndNewFolderHaveVisibleTooltipsLikeTheHiddenFilesToggle`.

## E4 — Dangerous options look exactly like benign ones

On the Tailcat hub, **"Insecure: allow any Tailcat client — Disable the Tailcat
client-key allowlist for this exit node"** is rendered in the same card, weight
and colour as **"Share a folder"**. The only warning is a low-contrast grey note
far below. Separately, **"Exit node" is checked by default**, which turns the
phone into a route for other devices unless the user notices and unchecks it.

**Fix:** a distinct treatment for security-reducing toggles (warning colour,
inline consequence text, confirmation on enable), and review the default.

- [x] **Landed** in PR #108 (commit `ac58122`), belongs with the A-list per
  the doc's own note. `TailcatPhonePanel.razor`'s `_exitNode` field now
  defaults to `false` (with a comment explaining why), and the insecure
  toggle carries `class="is-risky"` plus a `forward-option--risky` wrapper
  (danger rail + wash in `controls.css`), distinct from the plain
  `forward-option` around "Share a folder". Added regression coverage here:
  `UiUxAuditFollowUpTests.TailcatExitNodeIsUncheckedByDefault` and
  `...InsecureExitNodeToggleIsVisuallyDistinctFromABenignToggle`. (The
  existing `TailcatHubTests`/`KeysTests`/`TailcatCompletionTests` were
  already updated in `ac58122` to explicitly check the exit node on, since
  they depend on it being active — evidence the default-off change is real,
  but they never asserted the *default itself*, which is what the new tests
  do.)

## E5 — The `segmented__option` selected state competes with primary actions

The selected segment ("This phone") uses `background: rgb(255, 138, 91)` — the
exact fill of a primary CTA. On the Tailcat hub the loudest element is a tab
selection, not an action.

**Fix:** a lighter selected treatment (tinted background, accent underline, or
accent text on a subtle fill).

- [x] **Landed** in PR #108 (commit `ac58122`), with one maintenance trap
  worth flagging rather than silently trusting. `controls.css` adds a new
  `.segmented__option--on { background: var(--accent-wash); ... }`, but the
  *original* full-accent-fill declaration for the same selector was left
  behind in `meowssh.css`. Both rules have identical specificity, so which
  one wins depends entirely on `<link>` order in `index.html`/`App.razor` —
  it currently works because `controls.css` loads after `meowssh.css`, not
  because the old rule was removed. Left the dead rule's *shape* in place
  (padding/radius still come from `.segmented__option`) but deleted its
  competing `background`/`color`/`font-weight`, with a comment explaining
  why re-adding them would silently win again if the load order ever
  changes. Covered by
  `UiUxAuditFollowUpTests.SelectedSegmentedOptionDoesNotUseThePrimaryButtonFill`,
  which asserts the actual computed background differs from the primary
  button's rather than trusting the cascade order to hold.

## E6 — Centred five-line body copy on the first-run screen

The setup screen's paragraph is centred across five lines. Centred text loses the
consistent left edge the eye returns to, and five lines is past where that costs
nothing.

**Fix:** left-align the paragraph; keep the heading and button centred.

- [x] **Landed** in PR #108 (commit `ac58122`). `.lock__body` in
  `meowssh.css` sets `text-align: left` (with a comment explaining the
  five-line reasoning verbatim), while `.lock__panel`/`.lock__title` stay
  centred. Added regression coverage here:
  `UiUxAuditFollowUpTests.SetupScreenBodyCopyIsLeftAlignedWhileHeadingStaysCentred`.

---

# Suggested order

1. **A1** — it breaks navigation.
2. **A2, A3, A4** — all visible on screens that sell or support the product.
3. **D1–D4** — one control pass plus the widened guard; fixes a long tail at once.
4. **C1, C2, C3, C4** — the four layouts that actually look broken.
5. **B1–B6** — duplicate controls; mostly deletions.
6. **E** — polish, except **E4**, which is a security-presentation issue and
   belongs with the A-list.
