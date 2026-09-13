# MeowSSH

An SSH and SFTP client for Android, built on
[Meowshell](https://github.com/Sniperlyf3/meowshell).

Reaches ordinary SSH servers over TCP, Tailscale SSH across a tailnet, and
machines with no open port at all over a tailcat address — all through one
engine, with every credential encrypted at rest behind biometrics.

## Status

Feature-complete enough to install and use. The vault, the host list, the key
manager and the SSH engine all work on a real phone; what is listed below as
unfinished is unfinished, not untried.

| Area | State |
| --- | --- |
| Vault: key hierarchy, sealing, backups, recovery codes | Done |
| Encrypted storage for hosts and keys, surviving a restart | Done |
| First-run setup, vault unlock, recovery on device | Done |
| Host list, host editor, key management UI | Done |
| Terminal (xterm.js), key bar, live resize | Done |
| SSH engine over Meowshell's agent | Done, proven against real OpenSSH |
| SFTP file manager | Done, proven against real OpenSSH |
| Trust-on-first-use for new hosts | Done, proven against real OpenSSH |
| Android app, installable APK | Builds in CI; not yet run on a device |
| Sync between devices | Schema designed, not implemented |

**278 tests**: 182 unit, 62 browser end-to-end, 15 against a real `sshd`, and
19 on an Android emulator against the real Keystore.

## How it is put together

```
src/MeowSSH.Core       Domain, vault crypto, platform abstractions. No UI, no Android.
src/MeowSSH.UI         Razor components and the design system. Shared by both hosts below.
src/MeowSSH.Android    The Android half of Core's abstractions: Keystore, biometric prompt.
src/MeowSSH.TestHost   Blazor Server host with fakes. Runs on Linux so CI can drive the UI.
src/MeowSSH.App        MAUI Blazor Hybrid Android app.
```

`MeowSSH.App`, `MeowSSH.Android` and `MeowSSH.Device.Tests` are deliberately
**not** in `MeowSSH.slnx`. They need the `maui-android` workload and the Android
SDK, several minutes of setup the unit and browser test jobs should not pay for.
CI builds them by path in their own jobs; `dotnet build` at the root stays fast
and workload-free.

The platform code lives in `MeowSSH.Android` rather than inside the app for a
practical reason as well as a tidy one: the device tests are themselves an
Android app, and one app head cannot reference another — they resolve different
runtime identifiers and the build collapses.

The split exists for one reason: the Android app's UI is ordinary Razor
components in a WebView, so the *same components* can be hosted in a Blazor
Server app and driven by Playwright on Linux. Interaction tests run in CI in
minutes with no emulator.

## Installing on a phone

CI publishes a debug-signed arm64 APK as the `meowssh-apk` artifact on every
run. Download it from the run's Artifacts section, unzip, and install:

```sh
adb install -r dev.sniperlyf3.meowssh-Signed.apk
```

Or copy the APK to the device and open it, allowing installs from that source.

Debug rather than release, because a debug build is signed with the Android
SDK's own debug key and can therefore be sideloaded at all; a release build
needs a keystore, which belongs in repository secrets rather than in the repo.

To build one locally you need the `maui-android` workload, a JDK, and the
Android SDK:

```sh
dotnet workload install maui-android
dotnet publish src/MeowSSH.App/MeowSSH.App.csproj \
  -c Debug -f net10.0-android36.0 -p:RuntimeIdentifier=android-arm64
```

### What the first build can and cannot do

The vault, the terminal, the file browser and the SSH engine are all real, and
hosts and keys now persist across restarts. What you can do on first launch:
create the vault, write down the recovery code, add hosts and credentials,
unlock with a fingerprint, and reconnect to a host already in `known_hosts`.

What is not finished:

- **Keyboard-interactive authentication is declined.** A challenge is a
  variable list of questions and needs a form this app does not have yet.
  Answering it with blanks would look to the server like a wrong password, so
  it is refused instead.
- **Sync between devices is designed, not built.** Every record already carries
  the revision, timestamp, origin device and tombstone it needs; nothing
  exchanges them.

## Security design

The details are in the code comments, which explain the reasoning rather than
restating the mechanism. In short:

- **Nothing is encrypted with the master key directly.** Every use derives a
  purpose-labelled subkey via HKDF, so a leaked backup key cannot open stored
  credentials.
- **The master key only ever exists wrapped**, in one copy per unlock route: the
  device's hardware key store (biometric-gated, non-exportable) and the user's
  recovery code. Adding an account later means adding a wrapped copy, never
  moving the key somewhere a server can see it.
- **Records are bound to their own identity.** AES-256-GCM with associated data
  covering the record's type, id and schema version, so an attacker with write
  access to the database cannot move a trusted host's password onto one they
  control.
- **A recovery code is mandatory, not optional.** Hardware keys die with the
  device; without a portable second wrap, a broken phone is a lost vault.
- **Backups carry the master key**, sealed under the recovery code, so a restore
  is a true restore. The Argon2id cost parameters live in the header and are
  authenticated, so they cannot be rewritten down to something brute-forceable.
- **Secrets are pinned, explicitly zeroed buffers**, never `string` — a .NET
  string cannot be overwritten once written. The vault is serialized by hand
  rather than through JSON for the same reason: a private key routed through a
  serializer exists as an interned base64 string that can never be cleared.

The file on disk adds four properties of its own:

- **Hosts and credentials are sealed as separate sections**, under keys derived
  for different purposes, so drawing the host list never derives the key that
  opens a password. Whole sections rather than row-per-row, because per-row
  ciphertext publishes how many hosts exist and roughly how long each label is.
- **Each section is bound to the save counter**, so an attacker holding
  yesterday's copy of the file cannot splice its host section into today's — the
  tag fails to verify. Sections are bound to their own name too, so the two
  cannot be swapped for each other.
- **Saves are atomic**: written beside the vault, flushed to disk, then renamed
  over it, so a phone killed mid-save has either the old vault or the new one and
  never half of either.
- **Deleting a credential wipes the secret from the bytes on the next save**,
  not whenever a tombstone is eventually pruned. A deleted password still
  readable on disk is the whole problem with deletion.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test tests/MeowSSH.Core.Tests          # unit tests
dotnet test tests/MeowSSH.UI.Tests            # Playwright end-to-end tests
sudo -E dotnet test tests/MeowSSH.Integration.Tests   # against a real sshd
```

The device tests need the Android workload, an SDK and a running emulator, so
they are not part of `dotnet test`. With a device or emulator attached:

```sh
dotnet build tests/MeowSSH.Device.Tests/MeowSSH.Device.Tests.csproj -c Debug
adb install -r tests/MeowSSH.Device.Tests/bin/Debug/net10.0-android36.0/*.devicetests-Signed.apk
adb shell am instrument -w \
  dev.sniperlyf3.meowssh.devicetests/dev.sniperlyf3.meowssh.devicetests.TestInstrumentation
```

They cover what has no desktop equivalent: that the Keystore really refuses to
export a key, that a vault written on the device is unreadable on disk, that a
deleted key cannot open what it sealed, and that the API-level guards around
`BiometricManager` and `KeyInfo.SecurityLevel` hold. The interactive prompt
itself is not covered — that needs a person, or UI automation this does not
have.

The integration tests start their own OpenSSH server on a free port with its own
host key, and skip where `sshd` is not installed. They need root to create the
test user and start the daemon.

To see the UI without an Android device:

```sh
dotnet run --project src/MeowSSH.TestHost
```

Scenarios are query parameters, so each browser test is a plain navigation
rather than a setup script:

| Query | Screen |
| --- | --- |
| *(none)* | Host list |
| `?setup` | First run: create the vault, show the recovery code |
| `?locked` | Vault unlock, including the recovery-code path |
| `?newhost` | The host editor, adding |
| `?keys` | Saved keys and passwords |
| `?files` | The SFTP file browser |

`?theme=light` or `?theme=dark` pins a palette so both can be driven.

If your machine already has a Chromium that Playwright did not install, point
the suite at it with `MEOWSSH_CHROMIUM=/path/to/chrome` instead of downloading
another.

## License

MIT.
