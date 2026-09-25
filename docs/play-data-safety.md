# Google Play Data Safety and Policy Checklist

This file is the release-owner checklist for the production Play listing. It is not legal advice and it is not a substitute for answering Play Console against the exact shipping build.

## Current shipping-surface inventory

### Permissions / platform capabilities

- `INTERNET` — required for SSH/SFTP, licensing and Tailcat networking.
- `CAMERA` — optional; used for user-invoked QR scanning.
- `USE_BIOMETRIC` — used to unlock/protect the local encrypted vault.
- `FOREGROUND_SERVICE` / special-use foreground service — used to keep an explicitly initiated terminal session alive in the background.
- Android VPN service — used only for the explicitly initiated full-device Tailcat VPN capability when that feature is included in the release.
- USB host capability — optional serial-console support.

CI now inspects the merged Release manifest, not only the source manifest. The hardened APK lane requires the session keepalive service, `FOREGROUND_SERVICE_SPECIAL_USE`, `foregroundServiceType="specialUse"`, and `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` to survive manifest merging. The production-shape VPN-excluded AAB lane additionally requires `TailcatVpnService` and the HEV tunnel native library to be absent.

## Data categories to review in Play Console

### Financial / purchase data

MeowSSH handles Google Play product IDs and purchase tokens for entitlement verification. Google handles payment-card data. The production declaration must distinguish purchase/entitlement metadata from payment information MeowSSH never receives.

### App activity

Local session history, command monitoring and terminal logging can contain app activity and user content. Current implementations are local; do not declare them as collected by the developer unless a shipping service actually transmits them off-device.

### Device or other identifiers

Play Integrity and licensing requests include package/build/device-integrity evidence and one-time request identifiers. Review Google's current Data safety definitions for what must be declared for the exact Play Integrity integration.

### Files and documents

SFTP reads/writes files chosen by the user. Local encrypted backup exports user-selected app data. These operations do not by themselves mean the developer collects those files.

### Photos/videos / camera

Camera frames are used for QR scanning only. Confirm scanner implementation does not persist or transmit frames.

### Network/VPN data

If full-device VPN ships, perform a separate policy review. The app can route user-selected device traffic through a tunnel, which is a materially different policy surface from an SSH client.

### Encrypted cloud backup (Pro Cloud)

Built as of 2026-09-24. Nothing is sent until the user turns it on. Facts to base the declaration on — the release owner still has to make the call in Play Console:

- **What is transmitted:** the vault file, already end-to-end encrypted on the device, sent over HTTPS. MeowSSH cannot decrypt it, and it contains app settings/configuration, which may include credentials.
- **Identifier:** a random locator derived from the recovery code. It is not linked to the account, device or purchase.
- **User control:** the user can delete their backups in the app at any time.
- **Retention:** the five most recent versions are kept.
- **Not collected:** cloud backup does not send plaintext hosts, usernames, passwords or keys, and does not send analytics.

### Encrypted cross-device sync (Pro Cloud)

Built as of 2026-09-25, and off until the user turns it on. It needs cloud backup to be on as well.

- **What is transmitted:** the same end-to-end encrypted vault file, sent whenever the vault changes and on unlock or periodic checks while the app is open.
- **What is stored:** only the latest synced copy, apart from the backup versions. There is no extra identifier beyond the backup's locator.
- **User control:** "Delete cloud backups" erases it too, and sync can be turned off per phone.

### Heartbeat monitors (Pro Cloud push monitoring)

Built as of 2026-09-25. Nothing is sent until the user creates a monitor.

- **What is transmitted:**
  - monitor names and schedules the user types;
  - a random per-install owner secret, sent as a bearer credential and stored by the server only as a hash;
  - periodic status checks from WorkManager while monitors exist.
- **Not transmitted:** host names, addresses, credentials, push tokens, or device identifiers.
- **User control:** delete a monitor in the app and it is removed immediately.
- **Background work:** the app schedules periodic work only while at least one monitor exists, and cancels it when the last is deleted.

### Ask AI (Pro Cloud hosted AI)

Built as of 2026-09-25, and off on the service until an operator enables it with a model provider key.

- **What is transmitted:** only text the user explicitly sends from the Ask AI panel: selected or recent terminal output, which the user can edit, plus an optional question. It goes to MeowSSHAPI and on to Anthropic's API.
  - This is the first feature where terminal contents leave the device.
  - It is user-initiated app content: likely "App activity → other user-generated content", processed for app functionality.
  - Declare it if the production build enables Ask AI.
- **Retention by MeowSSH:** none for the content. There are daily request counters per grant, which expire in two days.
- **Third party:** Anthropic processes the text to generate the answer.

Whether end-to-end encrypted user content still counts as "collected" is a Play policy question to review against Google's current Data safety guidance at submission time, not settled here.

## Required production answers before submission

The release owner must confirm, from the exact production AAB:

- whether any analytics/crash SDK has been added;
- whether any terminal/session/command contents leave the device;
- whether any hostnames, IPs or URLs are sent to MeowSSH-operated services;
- whether cloud backup/sync is enabled;
- whether push monitoring is enabled;
- whether hosted AI is enabled;
- whether full-device VPN is included/enabled;
- which Tailcat coordination/relay providers process connection metadata;
- the exact production `TAILCAT_DERP_MAP_URL` selected for the build;
- retention/deletion behavior for MeowSSHAPI logs and infrastructure logs;
- support/privacy contact address;
- public privacy-policy URL;
- public Terms-of-Service URL and completion of the legal-review placeholders in `TERMS.md`.

If any answer changes after the form is submitted, update the Play declaration before releasing the build that changes it.

## VPN release gate

Do not assume that an SSH application's VPN feature is automatically acceptable because the implementation is technically legitimate. Before a production release with full-device VPN enabled:

1. Review the current Google Play VPN policy and VpnService declaration requirements.
2. Confirm the primary app functionality and listing accurately explain the VPN feature.
3. Confirm traffic is not intercepted, monetized, profiled or redirected for unrelated purposes.
4. Prepare the in-app disclosure/consent language Play requires for the shipping behavior.
5. Confirm the Play Console VpnService declaration matches the implementation.
6. If policy eligibility is uncertain, ship v1 with the full-device VPN capability disabled rather than risking rejection of the whole product.

The production `Play Release` workflow defaults `include_tailcat_vpn` to `false`. Changing that input to `true` is a deliberate policy decision and must not be treated as a normal build toggle.

## Foreground-service release gate

The shipping app uses a special-use foreground service for user-initiated terminal-session continuity. The merged manifest currently describes that subtype as:

> Keeps a user-initiated terminal session alive while MeowSSH is backgrounded

Before release:

- keep the Play Console foreground-service declaration aligned with that actual behavior;
- explain that the service exists only while preserving a user-initiated SSH/terminal session in the background;
- do not describe unrelated background synchronization, monitoring or always-on behavior under this subtype;
- verify notification behavior meets current Android/Play requirements;
- retain the CI evidence showing the permission, service type and subtype property survived manifest merging.

## Privacy, Terms, relay and support publication checklist

Before production rollout:

- replace the placeholder contact in `PRIVACY.md`;
- complete legal review of `TERMS.md` and replace the legal entity, warranty/liability, governing-law/venue, and public-contact placeholders;
- publish the privacy policy and Terms of Service at stable HTTPS URLs accessible without login;
- set repository/environment variable `PRIVACY_POLICY_URL` to the exact public privacy-policy URL;
- set repository/environment variable `TERMS_OF_SERVICE_URL` to the exact public Terms URL;
- set repository/environment variable `SUPPORT_URL` to a stable public HTTPS support destination;
- set repository/environment variable `TAILCAT_DERP_MAP_URL` to the exact production DERP map selected for the release;
- ensure that DERP-map URL is public HTTPS, reachable, and returns valid JSON;
- if the production map differs from `https://tailcat.dev/derpmap.json`, name that exact custom endpoint in `PRIVACY.md` and describe the corresponding relay operator/infrastructure before release;
- link the privacy-policy URL in Play Console;
- keep the in-app Settings links pointed at the same Privacy, Terms, and Support destinations;
- ensure statements about Tailcat relays match the infrastructure actually used at launch;
- ensure statements about telemetry match the production build;
- ensure cloud language matches which tiers/features are actually for sale.

The production workflow refuses to build a release when any required public URL is missing, non-HTTPS, or unreachable; when the selected DERP map is unreachable or invalid JSON; when a custom production DERP map is not named in the privacy policy; when the privacy contact placeholder remains; or when any production legal placeholder remains in `TERMS.md`.

## Internal-track publication handoff

The `Play Release` workflow can stop after producing the verified signed AAB, or explicitly publish that same artifact to Google Play internal testing with `publish_internal=true`.

For an internal-track publication, confirm all of the following first:

- `ANDROID_KEYSTORE_BASE64`, alias and signing passwords are configured for the persistent upload key;
- `LICENSING_API_BASE_URL` and `LICENSING_PUBLIC_KEY_SUBJECT_PUBLIC_KEY_INFO_BASE64` point at the deployed production licensing service;
- `PRIVACY_POLICY_URL`, `TERMS_OF_SERVICE_URL`, and `SUPPORT_URL` are the public launch destinations;
- `TAILCAT_DERP_MAP_URL` is the deliberate production relay-map choice and the privacy policy matches it;
- `GCP_WORKLOAD_IDENTITY_PROVIDER` and `GCP_PLAY_PUBLISH_SERVICE_ACCOUNT` are configured for a least-privilege Play publishing service account;
- Play App Signing is configured and the backend trusts the **app signing** certificate, not merely the local upload certificate;
- the production backend is configured with the Play Console SHA-256 app-signing fingerprint. The backend accepts the human-readable hexadecimal fingerprint and normalizes it to the URL-safe Base64 digest returned by Play Integrity;
- `include_tailcat_vpn=false` unless the VPN policy gate above has been intentionally cleared;
- the requested `version_code` has never been used before.

The first application setup and any Play Console declarations that cannot be performed through the Android Publisher API still have to exist before an automated internal-track publication can succeed. Treat a failed internal publish as a release blocker to diagnose, not as a reason to bypass the workflow with a different artifact.

## Release evidence to retain

For each production candidate retain:

- source commit SHA;
- signed AAB artifact/hash;
- merged Android manifest;
- dependency list / SBOM where available;
- Data safety answers used for that release;
- VPN and foreground-service declaration answers;
- published privacy-policy revision/date;
- published Terms-of-Service revision/date;
- selected production DERP-map URL and corresponding relay operator/infrastructure;
- Play app-signing certificate fingerprint(s);
- production licensing endpoint and public entitlement-key fingerprint;
- internal-track upload result and version code when publication was requested.

This makes policy/security claims reproducible when a future release changes permissions, dependencies or hosted services.
