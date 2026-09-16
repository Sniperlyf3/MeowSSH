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

Re-run a merged-manifest inspection on the final signed AAB. Source manifest inspection alone is not enough because attributes/libraries can contribute manifest entries.

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
- retention/deletion behavior for MeowSSHAPI logs and infrastructure logs;
- support/privacy contact address;
- public privacy-policy URL.

If any answer changes after the form is submitted, update the Play declaration before releasing the build that changes it.

## VPN release gate

Do not assume that an SSH application's VPN feature is automatically acceptable because the implementation is technically legitimate. Before a production release with full-device VPN enabled:

1. Review the current Google Play VPN policy and VpnService declaration requirements.
2. Confirm the primary app functionality and listing accurately explain the VPN feature.
3. Confirm traffic is not intercepted, monetized, profiled or redirected for unrelated purposes.
4. Prepare the in-app disclosure/consent language Play requires for the shipping behavior.
5. Confirm the Play Console VpnService declaration matches the implementation.
6. If policy eligibility is uncertain, ship v1 with the full-device VPN capability disabled rather than risking rejection of the whole product.

## Foreground-service release gate

The shipping app uses a special-use foreground service for user-initiated terminal-session continuity. Before release:

- verify the service declaration survives into the merged manifest;
- verify the declared subtype matches actual behavior;
- verify notification behavior meets current Android/Play requirements;
- document the user-visible reason for the foreground service in the Play declaration if requested.

## Privacy-policy publication checklist

Before production rollout:

- replace the placeholder contact in `PRIVACY.md`;
- publish the policy at a stable HTTPS URL accessible without login;
- link that URL in Play Console;
- expose a Privacy entry inside the app or Settings screen;
- ensure statements about Tailcat relays match the infrastructure actually used at launch;
- ensure statements about telemetry match the production build;
- ensure cloud language matches which tiers/features are actually for sale.

## Release evidence to retain

For each production candidate retain:

- source commit SHA;
- signed AAB artifact/hash;
- merged Android manifest;
- dependency list / SBOM where available;
- Data safety answers used for that release;
- VPN and foreground-service declaration answers;
- published privacy-policy revision/date;
- signing certificate digest;
- production licensing endpoint and public entitlement-key fingerprint.

This makes policy/security claims reproducible when a future release changes permissions, dependencies or hosted services.
