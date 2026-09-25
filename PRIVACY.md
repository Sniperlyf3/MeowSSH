# MeowSSH Privacy Policy

_Last updated: 24 September 2026_

MeowSSH is an Android SSH/SFTP and remote-operations client. This policy describes what the app handles, what leaves the device, and which features may involve third-party services.

## Core SSH, SFTP, terminal and local configuration

Host profiles, credentials, private keys, session preferences, Tailcat configuration, Actions, logs and local backups are stored on the user's device. The MeowSSH vault encrypts sensitive stored configuration. MeowSSH does not operate a service that receives terminal contents, remote shell commands, passwords or private SSH keys as part of normal SSH/SFTP use. The one exception is Ask AI (below). It sends only text the user chooses, and only when they press a button.

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

Encrypted local backup exports the vault file to a location the user chooses on the device. It never leaves the device unless the user moves it.

**Encrypted cloud backup (MeowSSH Pro Cloud).** Nothing is uploaded unless the user turns cloud backup on and enters their vault recovery code to confirm it. After that, each "Back up now" uploads the vault file exactly as it is already encrypted on the device, over HTTPS, to the MeowSSH service.

- **What the service receives and keeps:** the encrypted file, its size and creation time, and an opaque locator for it. The file is encrypted on the device with a key the service never receives, so the service cannot read the hosts, usernames, passwords, private keys or anything else inside.
- **What identifies a backup:** a secret derived on the device from the user's recovery code. It is not linked to the user's Google account, email address, device identifiers or purchase details, and the recovery code itself is never uploaded or stored.
- **Proving the subscription:** each upload includes a signed Pro Cloud entitlement grant. That grant is not stored with the backup.
- **Retention:** the service keeps the five most recent versions, and each new upload replaces the oldest.
- **Deletion:** "Delete cloud backups" erases every stored version immediately. "Turn off on this phone" stops uploads and forgets the device's copy of the locator secret, but leaves stored versions in place so they can still be restored.
- **Restoring and account recovery:** a backup can be restored on any device from the recovery code alone, with or without an active subscription. MeowSSH cannot recover a lost recovery code, and without it a cloud backup can be neither found nor decrypted.

**Cross-device sync (MeowSSH Pro Cloud).** Sync is off until the user turns it on, and it can only be turned on while cloud backup is on for the vault, because it uses the same recovery-code identity to find the vault's other phones. Once on, the app uploads the vault file whenever it changes and checks for other phones' changes when the vault is unlocked, every few minutes while it stays open, and on "Sync now". Merging happens on each phone.

- **What the service receives and keeps:** one encrypted copy of the vault file, kept apart from the backup versions. Its size and a hash of the encrypted bytes, used to tell phones apart from stale copies, are also kept. As with backups, the service cannot read anything inside.
- **Retention:** only the latest synced copy. Each sync replaces it, and it never takes the place of a backup version.
- **Deletion:** "Delete cloud backups" also erases the synced copy immediately. A phone that finds its synced copy gone turns sync off rather than uploading the vault again. "Turn off on this phone" stops syncing on that phone only.

## Heartbeat monitors (MeowSSH Pro Cloud)

A heartbeat monitor watches a job the user runs, such as a nightly backup, from outside. The job requests a secret URL when it finishes, and if the requests stop or report a failure, the phone shows a notification. MeowSSH never connects to the user's servers for this and holds no SSH credentials for it.

- **What the service keeps per monitor:**
  - the name the user gives it;
  - how often it should run, and the grace time;
  - when it was created, last checked in, and last reported a failure;
  - a one-way hash of its ping URL's secret.

  The request itself is not logged beyond that timestamp. The IP address of whatever sends it is seen, as with any web request, but not stored.
- **Identity:** a random secret generated on the phone and kept in Android secure storage. It is not linked to the Google account, purchase, vault or device identifiers.
- **Checking for alerts:** while any monitor exists, Android runs a background check roughly every 15 minutes, including when MeowSSH is closed. The check asks the service for the monitors' states, and notifications are created on the phone. No push token or device identifier is sent to MeowSSH or Google for this.
- **Deletion:** deleting a monitor in the app removes it from the service immediately, and its URL stops working. Monitors remain until deleted, including if Pro Cloud lapses. They stay listed and deletable, but background alerts stop.

## Teams (MeowSSH Team)

A team lets its owner share host addresses with the people they invite. It is an address book only: no password, private key or other credential is ever shared, and each member signs in to a server with their own key or password from their own vault.

- **What the service keeps per team:**
  - the team's name;
  - each member's chosen display name, role (owner or member) and join date;
  - for each shared host: its label, host name or IP address, port and, if the owner includes one, a user name;
  - open invites, as one-way hashes of their codes, with their expiry times;
  - an activity log of changes to the team: who created it, who joined, left or was removed, which invites were created or revoked, and which hosts were shared or unshared, each with the time and the display name of whoever did it. It does not record connections or anything typed in a session.
- **Who sees what:** members see the team's name, members and shared hosts. Only the owner sees open invites and the activity log.
- **Which hosts can be shared:** plain SSH hosts reached by name or IP address. Tailcat addresses are never shared, because the address itself grants access. Proxy settings, jump hosts and credentials are never shared.
- **Identity:** a random secret generated on the phone and kept in Android secure storage, separate from the one heartbeat monitors use. It is not linked to the Google account, purchase, vault or device identifiers. Nothing is sent until the user starts or joins a team.
- **Deletion:** leaving a team, or being removed, deletes the member's entry immediately. Deleting a team deletes everything above, including the activity log, immediately. Hosts a member already added to their own list stay in their vault.

## Ask AI (MeowSSH Pro Cloud)

Ask AI explains terminal output or suggests a shell command. Nothing is sent until the user opens Ask AI and presses Explain or Suggest a command. Even then, the only thing sent is what the panel shows: the user's selection, or the last lines on screen, which the user can edit or clear first, plus any question they type. Host names, addresses, usernames, passwords and keys are not added to it.

- **Where it goes:** over HTTPS to the MeowSSH service, which passes it to Anthropic's Claude API to write the answer. Anthropic processes it under its own commercial terms and data policies.
- **What MeowSSH keeps:** nothing of the text or the answer. The service logs only outcome codes (for example "quota exceeded"), plus daily request counts per entitlement grant to enforce usage limits. Counts expire after two days.
- **Proving the subscription:** each request carries a signed Pro Cloud entitlement grant, which is not stored.
- **Suggested commands** are never run for the user. "Insert at prompt" types a single-line command without pressing Enter, and multi-line suggestions are not inserted.

Users should not send secrets through Ask AI. If terminal output contains a password or key, the user should remove it from the text box before sending.

## Diagnostics and analytics

MeowSSH can capture a sanitized local crash snapshot: exception type, sanitized message and stack trace, app version, platform, and a short list of recent high-level in-app actions. It does not include terminal contents, commands, host names, addresses, usernames, file names, passwords, private keys, host credentials or full purchase tokens. Capturing this snapshot happens entirely on the device and never uploads anything by itself.

From the in-app "Report a problem" screen, a user can export a local text report to review and share manually, with or without the sanitized crash snapshot attached, or press "Send anonymized crash report" to submit just that snapshot to the MeowSSH diagnostics service over HTTPS. Nothing is sent automatically or in the background; sending happens only when the user presses that button.

If the user turns on "Include an anonymous install id in exported or sent reports" (off by default), a random, opaque, per-install identifier is included with an exported or sent report. This identifier is never derived from the device, the signed-in account, or any other identifier this app uses elsewhere; it proves nothing about who is using the app and is never used to authenticate or authorize anything. It exists only so the maintainer can tell reports from one install apart from another and rate-limit abusive report volume. Leaving this setting off, or exporting/sending without attaching diagnostics, omits it entirely; resetting it replaces it with a new, unrelated identifier.

The diagnostics service retains an accepted report only long enough to triage it and does not intentionally accept or retain terminal contents, commands, passwords, private keys, host credentials or full purchase tokens.

This is a Data Safety-relevant feature. The Play Console Data Safety declaration for this app must be updated to reflect crash-diagnostics collection, and that submission reviewed, before a build with this feature is released to Play; that is a separate operator action from this document and is not represented as complete here.

## Data sharing and sale

MeowSSH does not sell user data. Data is shared only as required to provide user-requested functionality, such as communicating with Google Play for purchases or with remote hosts and networking infrastructure selected by the user.

## Security

Sensitive local configuration is stored in the encrypted MeowSSH vault. Android hardware-backed key storage is used for vault protection where supported. Device capabilities vary, so MeowSSH does not promise that every device uses a dedicated secure element or StrongBox.

No software can guarantee the security of a compromised device, remote host, third-party network or user-supplied server configuration.

## Retention and deletion

Local MeowSSH data remains on the device until the user deletes it, clears the relevant history, removes the app, or restores/replaces the local vault as applicable. Encrypted cloud backups and the synced copy remain on the MeowSSH service until the user deletes them or newer uploads replace them (see "Backups and cloud features"); removing the app does not delete them. Google Play, the MeowSSH licensing service, and third-party relay infrastructure may have separate operational log or retention practices. MeowSSH does not intentionally send terminal contents, passwords or private SSH keys to the licensing service.

## Children

MeowSSH is a professional remote-administration tool and is not designed for children.

## Changes

This policy may be updated when functionality changes, especially before hosted monitoring, analytics or other server-side features are introduced.

## Contact

A public support/privacy contact must be inserted here before the Play Store production listing is submitted.
