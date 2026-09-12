# MeowSSH

An SSH and SFTP client for Android, built on
[Meowshell](https://github.com/Sniperlyf3/meowshell).

Reaches ordinary SSH servers over TCP, Tailscale SSH across a tailnet, and
machines with no open port at all over a tailcat address — all through one
engine, with every credential encrypted at rest behind biometrics.

## Status

Early. The vault security core and the host list are in place and tested;
the terminal, file manager and Android host are next.

| Area | State |
| --- | --- |
| Vault: key hierarchy, sealing, backups, recovery codes | Done |
| Host list and vault unlock UI | Done |
| Terminal (xterm.js), key bar, live resize | Done |
| SSH engine over Meowshell's agent | Done, proven against real OpenSSH |
| Trust-on-first-use for new hosts | Blocked — see below |
| SFTP file manager | Done, proven against real OpenSSH |
| Android app, installable APK | Builds in CI; not yet run on a device |
| Encrypted storage for hosts and keys | Next |

**153 tests**: 108 unit, 31 browser end-to-end, 14 against a real `sshd`.

### Known gap: first connections to new hosts

`MeowshellAgentConnection` completes its handshake inside `ConnectAsync` and
exposes prompts as instance events, so a caller cannot subscribe before the host
key question is asked. A host absent from `known_hosts` therefore fails rather
than prompting. `docs/specs/meowshell-prehandshake-prompts.md` specifies the fix.
Connections to already-known hosts, and the host-key-changed warning, work.

## How it is put together

```
src/MeowSSH.Core       Domain, vault crypto, platform abstractions. No UI, no Android.
src/MeowSSH.UI         Razor components and the design system. Shared by both hosts below.
src/MeowSSH.TestHost   Blazor Server host with fakes. Runs on Linux so CI can drive the UI.
src/MeowSSH.App        MAUI Blazor Hybrid Android app.
```

`MeowSSH.App` is deliberately **not** in `MeowSSH.slnx`. It needs the
`maui-android` workload and the Android SDK, several minutes of setup the unit
and browser test jobs should not pay for. CI builds it by path in its own job;
`dotnet build` at the root stays fast and workload-free.

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

The vault, the terminal, the file browser and the SSH engine are all real. Two
things are not finished, and both are visible immediately:

- **Nothing is persisted.** Hosts live in memory and the vault is created fresh
  each launch. The encrypted store is the next piece of work.
- **New hosts cannot be trusted yet.** The engine raises its host key and
  password prompts during the handshake, and the released Meowshell package
  offers no way to subscribe before that happens, so they go unanswered and the
  connection is refused. `docs/specs/meowshell-prehandshake-prompts.md` specifies
  the fix; it is implemented upstream and waiting on a release.

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
  string cannot be overwritten once written.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test tests/MeowSSH.Core.Tests          # unit tests
dotnet test tests/MeowSSH.UI.Tests            # Playwright end-to-end tests
sudo -E dotnet test tests/MeowSSH.Integration.Tests   # against a real sshd
```

The integration tests start their own OpenSSH server on a free port with its own
host key, and skip where `sshd` is not installed. They need root to create the
test user and start the daemon.

To see the UI without an Android device:

```sh
dotnet run --project src/MeowSSH.TestHost
```

Append `?locked` for the vault unlock screen, and `?theme=light` or
`?theme=dark` to pin a palette.

If your machine already has a Chromium that Playwright did not install, point
the suite at it with `MEOWSSH_CHROMIUM=/path/to/chrome` instead of downloading
another.

## License

MIT.
