# MeowSSH UX and navigation audit

This document is the working contract for the commercial-polish pass across the whole application. It is intentionally broader than visual styling: navigation, control choice, information hierarchy and interaction count are part of launch quality.

## Product interaction rules

1. **Common intent gets the shortest path.** Once the user is on the relevant screen, routine actions should normally take one interaction; two is acceptable when a choice genuinely has to be made first.
2. **One stable control represents one stable piece of state.** A control must not silently change meaning because the current state changed. Mode switching should use a selector/menu whose selected value is the current mode, not a collection of buttons whose icon or meaning morphs.
3. **Use native semantic controls first.** Use a select/drop-down for a compact mutually-exclusive choice, a checkbox/switch for independent boolean state, radio/segmented controls only when keeping a small choice set visible materially helps comparison, and disclosure controls for optional detail.
4. **One obvious primary action per decision surface.** Secondary actions must not visually compete with the thing most users came to do.
5. **Progressively disclose advanced, uncommon and destructive actions.** Overflow/context menus, disclosure panels and focused sub-pages are preferred to permanently visible peer-level buttons.
6. **Settings roots navigate; detail screens configure.** The main Settings screen should summarize categories and current state. Rich controls, previews, history and specialist configuration belong on focused sub-pages.
7. **Keep navigation shallow and reversible.** A focused sub-page should have a predictable Back action, preserve the parent state and avoid forcing the user through a wizard unless order genuinely matters.
8. **Do not duplicate navigation concepts.** The same destination should not be represented by multiple unrelated button patterns within one workflow.
9. **Destructive actions are contextual and explicit.** They should be easy to find when managing the relevant object, but should not live beside the main positive action without separation and confirmation where appropriate.
10. **Phone width is a first-class layout.** No horizontal page overflow, crowded peer controls or tiny touch targets. Dense desktop-style toolbars should collapse to menus/selectors.
11. **Regression tests protect interaction design.** Important flows should assert control count, selected state, one-tap/common-path behavior, progressive disclosure and narrow-width layout—not only final functionality.

## App-wide audit inventory

| Area | Current direction | Commercial-polish target | Priority |
| --- | --- | --- | --- |
| Session workspace | Session tabs plus separate/morphing Files and Forwards buttons | One active-view drop-down showing Terminal / Files / Forwards; session tabs only select/close sessions | P0 |
| New/Edit connection | Core connection fields mixed with Group, Tags, Favorite, jump host, agent forwarding, proxy and reconnect | Keep the normal connect path visible; group organisation metadata and advanced connection options behind focused disclosure/sub-pages | P0 |
| Settings | Improved: Appearance already has a previewable sub-page; some plan/session/support controls still live together | Category-oriented root with concise state summaries; appearance, session data/logging, plan/billing and support/diagnostics as focused destinations | P1 |
| Files / SFTP | Compact item actions, but top bar and growing Pro functionality can accumulate controls | Keep browse/upload primary; move uncommon display/search/transfer management into a compact overflow or focused transfer/history surface | P1 |
| Keys / credentials | Feature-rich page combines several credential/key workflows | Separate browse/manage from Create/Import/Hardware/backup flows; contextual item actions rather than a permanent action wall | P1 |
| Actions | Creation, templates, multi-step behavior and execution can become dense | Separate library, editor and run/result states; one obvious Run action; advanced step behavior disclosed in editor only | P1 |
| Port forwarding | Dedicated page is appropriate, but profiles and add/edit state share space | Preserve fast “start a forward” path; profiles/history/configuration use focused disclosure without extra permanent toolbar actions | P1 |
| Tailcat | Large capability surface spread across panels | Group by user intent (Connect, Devices/Keys, Network/VPN, Transfers, Diagnostics) and avoid exposing implementation concepts as equal top-level actions | P1 |
| Session logs / monitoring | Already separate specialist pages | Keep them out of root screens except concise entry/status cards | P2 |
| Crash / bug reports | Privacy-first local snapshot/store and consent work in progress | Prompt only when relevant; two default choices; inspectable details; diagnostics/support destination for manual bug reports | P0 launch quality |
| Vault setup/unlock | Focused flows | Preserve minimal unlock path; recovery/security detail appears only when required | P2 |

## Interaction budgets for critical flows

These are targets used during the audit, not arbitrary hard limits when security or destructive confirmation requires another step.

- Open a saved host: **1 tap** from Hosts.
- Switch an active SSH session between Terminal, Files and Forwards: **1 selector interaction**.
- Return to another open session: **1 tap** on its tab.
- Upload a file from an open SFTP directory: **1 tap**, followed by the platform file picker.
- Run an already-configured Action: **1 tap** (plus required parameter confirmation only when the action actually has parameters).
- Change terminal theme: Settings → Appearance, then **1 theme selection**, with preview visible before leaving.
- Dismiss/defer a crash report: **1 tap**.
- Send an already-reviewed anonymized crash report: **1 tap** from the consent surface.

## Implementation order

### P0 — remove inconsistent navigation semantics

- [x] Dedicated Appearance settings/page with previews instead of a growing Settings form.
- [ ] Replace Session Terminal/Files/Forwards morphing buttons with one active-view selector.
- [ ] Simplify New/Edit Connection into a short common path plus Organisation and Advanced Connection disclosure.
- [ ] Finish consent-driven crash-report capture/review/send flow and add manual Bug report entry under Support/Diagnostics.

### P1 — reduce toolbar and root-screen density

- [ ] Review Files/SFTP toolbar and Pro transfer/search controls; keep browse/upload primary and move secondary tools behind an overflow/focused page.
- [ ] Review Keys/Credentials navigation; split create/import/hardware/backup concerns from day-to-day key management.
- [ ] Review Actions library/editor/run-result navigation and remove peer controls that are only relevant while editing.
- [ ] Review Settings root information architecture; move detailed toggles to their owning sub-pages and keep concise status summaries.
- [ ] Review Port Forwarding profile management and creation flow.
- [ ] Review Tailcat panels and group by user intent instead of feature implementation.

### P2 — consistency and polish pass

- [ ] Audit every top bar, contextual action sheet, confirmation sheet and empty state for consistent action hierarchy and labels.
- [ ] Ensure Back/Cancel semantics are consistent and never unexpectedly destroy draft state.
- [ ] Verify all phone-width screens for overflow and minimum comfortable touch targets.
- [ ] Add or extend Playwright regressions for control budgets and common-path interaction counts.
- [ ] Accessibility pass for labels, selected/current state, keyboard focus and native control semantics.

## Definition of done for a reviewed screen

A screen is not considered polished merely because it looks good. It is done when the common task is obvious, common actions require the minimum reasonable interactions, advanced controls do not crowd the default state, state-changing controls have stable semantics, destructive actions are separated, navigation is predictable, phone-width layout is clean, accessibility state is exposed correctly, and automated tests protect the intended interaction model.
