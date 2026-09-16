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
| Session workspace | Reviewed: one active-view selector controls Terminal / Files / Forwards | Preserve one stable selector; session tabs only select/close sessions | P0 |
| New/Edit connection | Reviewed: normal connection path plus Organisation and Advanced connection disclosure | Keep the normal connect path visible; keep metadata and specialist connection controls progressively disclosed | P0 |
| Settings | Reviewed: category/status root with focused plan, logs, appearance and support/detail pages | Keep the root concise and route rich configuration to focused destinations | P1 |
| Files / SFTP | Reviewed: normal host browsing is primary; Advanced SFTP is contextual per host | Keep browse/upload primary; keep search/bookmarks/recursive tooling contextual | P1 |
| Keys / credentials | Reviewed: focused Generate / Import / Device / Public / Password flows and contextual saved-key management | Keep creation concerns separate from day-to-day key management | P1 |
| Actions | Reviewed: Run remains primary; management and sequence configuration are contextual | Keep result viewing separate from the library state and specialist editing controls out of the run path | P1 |
| Port forwarding | In review: common route fields stay immediate; saved profiles and specialist listener tuning move behind disclosure | Preserve fast “start a forward” path; profiles/history/configuration remain secondary | P1 |
| Tailcat | In review: feature panels grouped by user intent | Group by Connect, Devices/Keys, Network/VPN, Transfers and Diagnostics without removing capability | P1 |
| Session logs / monitoring | Already separate specialist pages | Keep them out of root screens except concise entry/status cards | P2 |
| Crash / bug reports | Reviewed: privacy-first local crash capture with explicit review/export/discard and manual diagnostics entry | Preserve consent-first local-only behavior unless the user explicitly chooses an export | P0 launch quality |
| Vault setup/unlock | Focused flows | Preserve minimal unlock path; recovery/security detail appears only when required | P2 |

## Interaction budgets for critical flows

These are targets used during the audit, not arbitrary hard limits when security or destructive confirmation requires another step.

- Open a saved host: **1 tap** from Hosts.
- Switch an active SSH session between Terminal, Files and Forwards: **1 selector interaction**.
- Return to another open session: **1 tap** on its tab.
- Upload a file from an open SFTP directory: **1 tap**, followed by the platform file picker.
- Run an already-configured Action: **1 tap** (plus required parameter confirmation only when the action actually has parameters).
- Change terminal theme: Settings → Appearance, then **1 theme selection**, with preview visible before leaving.
- Dismiss/discard a crash report: **1 tap**.
- Save an already-reviewed sanitized crash report: **1 tap** from the consent surface.

## Implementation order

### P0 — remove inconsistent navigation semantics

- [x] Dedicated Appearance settings/page with previews instead of a growing Settings form.
- [x] Replace Session Terminal/Files/Forwards morphing buttons with one active-view selector. (PR #90)
- [x] Simplify New/Edit Connection into a short common path plus Organisation and Advanced Connection disclosure. (PR #93)
- [x] Finish consent-driven local crash-report capture/review/export flow and add manual Privacy & diagnostics entry. (PRs #96–#97)

### P1 — reduce toolbar and root-screen density

- [x] Review Files/SFTP toolbar and Pro transfer/search controls; keep browse/upload primary and move secondary tools behind contextual disclosure. (PR #99)
- [x] Review Keys/Credentials navigation; split Generate / Import / Device / Public / Password concerns from day-to-day key management. (PR #98)
- [x] Review Actions library/editor/run-result navigation and remove peer controls that are only relevant while editing. (PR #95)
- [x] Review Settings root information architecture; move detailed toggles to their owning sub-pages and keep concise status summaries. (PR #94)
- [ ] Review Port Forwarding profile management and creation flow. (PR #100 in validation)
- [ ] Review Tailcat panels and group by user intent instead of feature implementation. (PR #101 in validation)

### P2 — consistency and polish pass

- [ ] Audit every top bar, contextual action sheet, confirmation sheet and empty state for consistent action hierarchy and labels.
- [ ] Ensure Back/Cancel semantics are consistent and never unexpectedly destroy draft state.
- [ ] Verify all phone-width screens for overflow and minimum comfortable touch targets.
- [ ] Add or extend Playwright regressions for control budgets and common-path interaction counts.
- [ ] Accessibility pass for labels, selected/current state, keyboard focus and native control semantics.

## Definition of done for a reviewed screen

A screen is not considered polished merely because it looks good. It is done when the common task is obvious, common actions require the minimum reasonable interactions, advanced controls do not crowd the default state, state-changing controls have stable semantics, destructive actions are separated, navigation is predictable, phone-width layout is clean, accessibility state is exposed correctly, and automated tests protect the intended interaction model.
