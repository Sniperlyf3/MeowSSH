# MeowSSH Privacy Policy

_Last updated: 16 September 2026_

MeowSSH is an Android SSH/SFTP and remote-operations client. This policy describes what the app handles, what leaves the device, and which features may involve third-party services.

## Core SSH, SFTP, terminal and local configuration

Host profiles, credentials, private keys, session preferences, Tailcat configuration, Actions, logs and local backups are stored on the user's device. The MeowSSH vault encrypts sensitive stored configuration. MeowSSH does not operate a service that receives terminal contents, remote shell commands, passwords or private SSH keys as part of normal SSH/SFTP use.

Connections to hosts chosen by the user necessarily send network traffic to those hosts and to any network infrastructure required to reach them.

## Google Play purchases and licensing

When paid features are purchased or restored, MeowSSH communicates with Google Play and the MeowSSH licensing service. The licensing flow may process:

- Google Play product identifiers and purchase tokens;
- the app package identifier;
- a short-lived Play Integrity token and its validation result;
- a one-time request identifier used to prevent replay;
- entitlement tier and signed entitlement metadata.

Purchase tokens are used for entitlement verification and must not be written to application logs. MeowSSH does not receive the user's payment-card details; payment is handled by Google Play.

## Camera

The optional camera permission is used only when the user invokes QR-code scanning features. Camera imagery is processed for scanning and is not intentionally uploaded by MeowSSH.

## VPN / full-device routing

If the user explicitly enables MeowSSH's full-device Tailcat VPN feature, Android routes selected device network traffic through the user-configured tunnel. This can expose destination addressing and traffic metadata to the network infrastructure used by that tunnel. MeowSSH does not claim that enabling a VPN makes third-party destinations anonymous.

The VPN feature is user-initiated and can be stopped by the user. MeowSSH does not use VPN traffic for advertising, profiling or sale of personal information. The first public Play release is configured to exclude this full-device VPN capability unless it is deliberately enabled for a later release after the applicable policy review.

## Tailcat and relay infrastructure

MeowSSH uses the open-source Tailcat data plane for peer-to-peer networking. The reviewed Tailcat revision and normal app-build default use the upstream Tailcat DERP map at `https://tailcat.dev/derpmap.json`. Production Play releases do not silently inherit that default: the release workflow requires an explicit `TAILCAT_DERP_MAP_URL`, validates that it is reachable HTTPS JSON, and records that selected endpoint in the release evidence.

If the production release uses `https://tailcat.dev/derpmap.json`, Tailcat can use the upstream third-party relay infrastructure described below. If a different production DERP map is selected, this published policy must be updated before release to identify that exact endpoint and the corresponding relay operator/infrastructure; the release workflow rejects a custom map that is not named in this policy.

Tailcat uses DERP to bootstrap connectivity and as a fallback relay when a direct peer-to-peer UDP path cannot be established. The upstream Tailcat project describes its default DERP relays as free and rate-limited. Connection metadata required to select and reach a relay, and encrypted tunnel packets when relay fallback is used, can therefore be processed by that third-party Tailcat/Tailscale infrastructure. Application payloads inside the Tailcat tunnel remain protected by Tailcat's WireGuard-based end-to-end encryption.

Users can supply an alternate DERP map for supported Tailcat operations, including a map for self-hosted relay infrastructure. When an alternate map is used, that map and its listed relay operators process the corresponding connection metadata instead of the default map/relays.

## Local session logs and monitoring

Session logging and command-monitoring features are local features unless a future cloud feature explicitly states otherwise. Terminal sessions can contain sensitive material, so persistent logging must remain opt-in or clearly user-enabled. Users can clear locally stored history.

## Backups and cloud features

The current backup feature is local and encrypted. MeowSSH Pro Cloud is not offered for new purchase in the production build until cloud backup/sync is implemented and this policy has been updated to document what is uploaded, retention, deletion and account recovery behavior.

Any future cloud-sync design should encrypt user configuration on the client before upload where practical, and must not require the service to receive plaintext private SSH keys.

## Diagnostics and analytics

MeowSSH should not collect terminal contents, commands, passwords, private keys, host credentials or full purchase tokens as crash/analytics payloads. If crash reporting or product analytics is added, this policy and the Play Data safety declaration must be updated before the telemetry-enabled build is released.

## Data sharing and sale

MeowSSH does not sell user data. Data is shared only as required to provide user-requested functionality, such as communicating with Google Play for purchases or with remote hosts and networking infrastructure selected by the user.

## Security

Sensitive local configuration is stored in the encrypted MeowSSH vault. Android hardware-backed key storage is used for vault protection where supported. Device capabilities vary, so MeowSSH does not promise that every device uses a dedicated secure element or StrongBox.

No software can guarantee the security of a compromised device, remote host, third-party network or user-supplied server configuration.

## Retention and deletion

Local MeowSSH data remains on the device until the user deletes it, clears the relevant history, removes the app, or restores/replaces the local vault as applicable. Google Play, the MeowSSH licensing service, and third-party relay infrastructure may have separate operational log or retention practices. MeowSSH does not intentionally send terminal contents, passwords or private SSH keys to the licensing service.

## Children

MeowSSH is a professional remote-administration tool and is not designed for children.

## Changes

This policy may be updated when functionality changes, especially before cloud sync, hosted monitoring, analytics or other server-side features are introduced.

## Contact

A public support/privacy contact must be inserted here before the Play Store production listing is submitted.
