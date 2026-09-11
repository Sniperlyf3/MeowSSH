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
| Vault: key hierarchy, sealing, backups, recovery codes | Done, 57 tests |
| Host list and vault unlock UI | Done, 12 end-to-end tests |
| Meowshell agent integration | Next |
| Terminal (xterm.js) | Next |
| SFTP file manager | Planned |
| Android app host | Planned |

## How it is put together

```
src/MeowSSH.Core       Domain, vault crypto, platform abstractions. No UI, no Android.
src/MeowSSH.UI         Razor components and the design system. Shared by both hosts below.
src/MeowSSH.TestHost   Blazor Server host with fakes. Runs on Linux so CI can drive the UI.
src/MeowSSH.App        MAUI Blazor Hybrid Android app. (planned)
```

The split exists for one reason: the Android app's UI is ordinary Razor
components in a WebView, so the *same components* can be hosted in a Blazor
Server app and driven by Playwright on Linux. Interaction tests run in CI in
minutes with no emulator.

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
dotnet test tests/MeowSSH.Core.Tests     # unit tests
dotnet test tests/MeowSSH.UI.Tests       # Playwright end-to-end tests
```

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
