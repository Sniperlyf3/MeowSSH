# Google Play Store Listing — v1 Launch Draft

_Last reviewed against Google Play guidance: 16 September 2026_

This document is the source-controlled handoff for MeowSSH's first public Google Play listing. It deliberately describes only functionality intended to exist in the public v1 build. Do not add planned Cloud, hosted AI, push-monitoring, Team, or full-device VPN claims until those capabilities are actually shipping and their policy/privacy work is complete.

Official guidance used for this checklist:

- https://support.google.com/googleplay/android-developer/answer/9859152 — app name, short-description, full-description and contact requirements
- https://support.google.com/googleplay/android-developer/answer/9898842 — metadata accuracy and prohibited ranking/price claims
- https://support.google.com/googleplay/android-developer/answer/9866151 — preview-asset and screenshot requirements
- https://support.google.com/googleplay/android-developer/answer/10144311 — privacy-policy requirements

## Main listing copy

### App name

`MeowSSH`

- 7 characters; Play limit is 30.
- Keep the title free of pricing, ranking, promotional or unrelated keyword claims.

### Short description

`SSH, SFTP, terminal, keys and secure remote tools for Android`

- 61 characters including spaces; Play limit is 80.
- A single-sentence short description is intentionally left without a trailing full stop to match current Play formatting guidance.
- Do not add `free`, discount language, rankings, competitor names, calls to action or unsupported features.

### Full description

> MeowSSH is an Android SSH and SFTP client built for secure remote administration.
>
> Connect to Linux servers and other SSH hosts, work in a full terminal, browse and transfer files with SFTP, manage SSH credentials and keys, and configure port forwarding from one app.
>
> MeowSSH also includes Tailcat-based peer-to-peer networking tools for supported remote-access workflows, reusable host organization, reusable command Actions, local session history and monitoring tools, encrypted local backup, and advanced SFTP workflows where available in your plan.
>
> Security-focused features include an encrypted local vault, biometric protection where supported, SSH host-key handling, and optional non-exportable Android hardware-backed SSH keys on compatible devices.
>
> Core SSH, SFTP, host management, keys and basic remote-access tools are available without a paid plan. MeowSSH Pro unlocks additional local productivity and administration features through a one-time Google Play purchase. Paid access is verified through Google Play and the MeowSSH licensing service.
>
> MeowSSH does not require its licensing service to receive your terminal contents, passwords or private SSH keys during normal SSH/SFTP use. See the in-app Privacy Policy for details about purchases, Tailcat relay infrastructure and data handling.

Before copying this text into Play Console, verify every named feature against the exact production AAB. Remove any statement that is not true of that build.

## Claims intentionally excluded from public v1

Do **not** advertise these until separately shipped and cleared:

- Pro Cloud, cloud sync or cloud backup;
- hosted AI features;
- push/background hosted monitoring;
- Team sharing/audit functionality;
- full-device Tailcat VPN;
- self-hosted MeowSSH relay infrastructure unless it actually exists and the privacy policy matches it;
- uptime, anonymity, security guarantees, benchmark/ranking claims, or claims of endorsement by Tailscale or another third party.

The app may contain gated/disabled implementation groundwork for later features. Store metadata must describe the production behavior users can actually access, not dormant code.

## Screenshot plan

Google Play requires at least two screenshots across supported device types. For a strong phone listing, capture at least four high-resolution phone screenshots at 9:16, preferably at least 1080 × 1920. Screenshots must depict the real app UI from the production-equivalent build.

Recommended v1 sequence:

1. **Hosts** — populated host list showing groups/tags/favorites/search without real customer/server data.
2. **Terminal** — active SSH terminal using a dedicated demo host and non-sensitive sample command/output.
3. **SFTP** — file browser showing upload/download/edit/permissions workflow with synthetic files.
4. **Keys** — SSH key management, preferably showing the device-key option without exposing a real production public key if that key is operationally sensitive.
5. **Port forwarding or Actions** — show one meaningful remote-administration workflow actually shipping in v1.
6. **Tailcat** — only a workflow available in the public production build; do not imply full-device VPN if it is excluded.

Screenshot hygiene:

- use synthetic hostnames, IP addresses, usernames, files, keys and terminal output;
- remove unrelated notifications and identifying status-bar data where practical;
- do not show passwords, private keys, purchase tokens, recovery codes, real infrastructure addresses or customer data;
- keep additional promotional overlay text minimal; if added later, localize it with the listing;
- do not add price, discount, ranking, `#1`, award, Google Play badge, competitor-logo or endorsement claims;
- add useful alt text in Play Console.

## Required Play assets / fields

Before submission, verify:

- [ ] App name ≤ 30 characters.
- [ ] Short description ≤ 80 characters.
- [ ] Full description ≤ 4,000 characters.
- [ ] 512 × 512, 32-bit PNG Play icon, ≤ 1,024 KB.
- [ ] Required feature graphic: 1024 × 500 JPEG or 24-bit PNG with no alpha.
- [ ] Minimum two screenshots uploaded; target at least four high-resolution phone screenshots.
- [ ] Screenshots reflect the exact public build and contain no secrets or real customer data.
- [ ] Public store-listing contact email is configured. **Do not invent one in source control.**
- [ ] Public support website/destination is configured.
- [ ] Public privacy-policy URL is configured in Play Console and accessible from the app.
- [ ] Public Terms-of-Service URL is published and accessible from the app once the Terms launch gate lands.
- [ ] Ads declaration matches the production build.
- [ ] App access/reviewer instructions cover any functionality that cannot be evaluated without credentials or a remote host.
- [ ] Data Safety answers match the production AAB and `docs/play-data-safety.md`.
- [ ] Foreground-service declaration matches the user-initiated terminal keepalive service.
- [ ] Full-device VPN remains excluded unless its separate policy gate has deliberately been cleared.

## Reviewer/demo environment

Prepare a dedicated, disposable review/demo SSH target rather than supplying production credentials. The reviewer instructions should identify enough steps to exercise representative functionality without granting access to real systems.

Do not commit review passwords, private keys, purchase test credentials or production endpoint secrets to this repository. Store sensitive reviewer credentials only in the appropriate Play Console access-instructions field or other approved secret channel.

## Final release cross-check

Immediately before production submission:

1. Build the release AAB through `.github/workflows/play-release.yml`.
2. Compare this listing line-by-line with the exact artifact's enabled features.
3. Confirm Pro Cloud sales remain disabled unless cloud functionality has actually launched.
4. Confirm the listing does not promise the full-device VPN if `include_tailcat_vpn=false`.
5. Confirm the selected production Tailcat DERP infrastructure matches the published privacy policy.
6. Confirm Privacy, Terms and Support destinations are the same public destinations used by the release build.
7. Retain the final listing text and screenshot set with the release evidence for that version.
