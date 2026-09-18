# Spec: UI/UX defects found in a full-app screenshot audit

**Repository:** `Sniperlyf3/MeowSSH`
**Affects:** `src/MeowSSH.UI/Pages/`, `src/MeowSSH.UI/Components/`, `src/MeowSSH.UI/wwwroot/css/`, `tests/MeowSSH.UI.Tests/`
**Status:** A-series (A1–A4) landed in PR #108, commit `ac58122`; B–E remain proposed.
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

The header `+` and the empty-state call to action both resolve to the label
`Add credential`, visible simultaneously.

**Fix:** the empty state owns the action; drop the header `+` while the list is
empty, and drop the empty state once it is not.

## B2 — "Advanced SFTP PRO" appears six times on the Files landing page

One per host row. Six identical buttons on one screen, all doing the same thing
to a different host.

This is a symptom of the bigger problem in C3: every host row is followed by a
`More` band that expands into a per-host duplicate of the same tool.

**Fix:** promote Advanced SFTP to a single entry point (the Tools hub or the
Files header) that asks which host afterwards, the way the rest of the app
already does.

## B3 — Two different visible controls both labelled "Keys"

On the Tailcat hub, a `segmented__option` labelled **Keys** (Tailcat identities)
sits a few hundred pixels above the tab bar's **Keys** tab (SSH credentials).
Same word, same screen, two unrelated destinations.

**Fix:** rename the Tailcat one to "Tailcat identities" (or "Device keys").

## B4 — Card heading, description and button all say the same word

On the Tailcat hub, four cards follow this shape:

```
Transfers
Browse and transfer files over Tailcat.
[ Transfers ]
```

identically for **Diagnostics**, **VPN**, and **Connect**. Three lines of chrome
plus a bordered card to deliver one tap target, with the label stated twice.

**Fix:** demote these to single list rows — icon, title, one-line description, a
chevron, whole row tappable. Reserve the card treatment for sections that
genuinely contain multiple controls (`Devices & keys`, `Share this phone`).

## B5 — Tools hub cards are tappable *and* carry an "Open" button

Every card on the Tools hub is a `<button>` with a nested "Open" affordance. Two
hit targets, one destination, and the nested one invites the user to think the
rest of the card does something else.

**Fix:** keep the whole card tappable; replace "Open" with a chevron.

## B6 — Two tab-bar icons are identical

`Tabs` in `AppShell.razor`:

```csharp
new("tools", "Tools", "settings"),
new("settings", "Settings", "settings"),
```

Tools and Settings render the same gear. At a glance the primary navigation has
two indistinguishable destinations.

**Fix:** give Tools its own icon (a wrench, a toolbox, a grid).

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

## C3 — A "More" band after every host row doubles the Files list

The Files landing page renders each host row followed by a full-width band
containing a single left-aligned "More" (55×40) against roughly 80% dead space.
Six hosts produce six of these. It roughly doubles the list height and reads as
an unfinished component.

**Fix:** collapse into the host row itself — a chevron or overflow control on the
row, or a bottom sheet on long-press. See also B2.

## C4 — The SFTP overflow column reads as a detached stripe

In the file browser the trailing "…" controls sit in a column with its own
background that runs the full height of the card, while the per-row separators
stop short of it. It looks like a scrollbar or a rendering artefact rather than a
per-row menu, and it occupies ~85px for a control that needs ~44px.

**Fix:** put the "…" inside the row, sharing the row's background and separators.

## C5 — Text inside Settings cards is centre-aligned; everywhere else is not

Settings card titles and descriptions are centred, so the ragged edges of
multi-line wraps make the column hard to scan, and the section reads as a
different app from the left-aligned hosts and files lists.

**Fix:** left-align card content throughout.

## C6 — Actions hug their content and leave a ragged right edge

On `Settings → Plan`, "Refresh plan" and "Restore purchases" are different widths
because each is sized to its label, stacked left-aligned.

**Fix:** stack full-width, or set them side by side on one row with equal widths.

## C7 — Several screens are mostly empty below the fold

The hosts list fills about half the viewport and the rest is ground. Actions,
Monitoring and Host health are a heading and one button. This is partly fixture
data, but the layouts make no use of the space either way — no summary, no recent
activity, no secondary affordance.

**Fix (lower priority than the rest):** consider what each screen should say when
it has little to show, beyond an empty state. Host health in particular is one
orange button on an otherwise blank page.

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

---

# E. Copy, semantics and hierarchy

## E1 — A status and an action are styled identically

On the Settings root, `Customize` and `Open` are actions; `Off` is a status. All
three are the same accent-coloured text in the same position.

**Fix:** statuses as neutral text or a pill; actions keep the accent and a
chevron.

## E2 — "PRO" is baked into heading text

"Session logs PRO", "Advanced SFTP PRO" — the tier marker is part of the title
string, so it inherits heading weight and reads as part of the feature's name.

**Fix:** a small badge component beside the title, styled once.

## E3 — The Files toolbar is three unlabelled icons

Upload, a crossed-out eye, and new-folder, with no labels or tooltips. The
crossed-out eye is genuinely ambiguous: it could mean "hidden files are hidden"
or "tap to hide". State matters and the icon does not carry it.

**Fix:** labels, or an explicit two-state control ("Hidden files: off").

## E4 — Dangerous options look exactly like benign ones

On the Tailcat hub, **"Insecure: allow any Tailcat client — Disable the Tailcat
client-key allowlist for this exit node"** is rendered in the same card, weight
and colour as **"Share a folder"**. The only warning is a low-contrast grey note
far below. Separately, **"Exit node" is checked by default**, which turns the
phone into a route for other devices unless the user notices and unchecks it.

**Fix:** a distinct treatment for security-reducing toggles (warning colour,
inline consequence text, confirmation on enable), and review the default.

## E5 — The `segmented__option` selected state competes with primary actions

The selected segment ("This phone") uses `background: rgb(255, 138, 91)` — the
exact fill of a primary CTA. On the Tailcat hub the loudest element is a tab
selection, not an action.

**Fix:** a lighter selected treatment (tinted background, accent underline, or
accent text on a subtle fill).

## E6 — Centred five-line body copy on the first-run screen

The setup screen's paragraph is centred across five lines. Centred text loses the
consistent left edge the eye returns to, and five lines is past where that costs
nothing.

**Fix:** left-align the paragraph; keep the heading and button centred.

---

# Suggested order

1. **A1** — it breaks navigation.
2. **A2, A3, A4** — all visible on screens that sell or support the product.
3. **D1–D4** — one control pass plus the widened guard; fixes a long tail at once.
4. **C1, C2, C3, C4** — the four layouts that actually look broken.
5. **B1–B6** — duplicate controls; mostly deletions.
6. **E** — polish, except **E4**, which is a security-presentation issue and
   belongs with the A-list.
