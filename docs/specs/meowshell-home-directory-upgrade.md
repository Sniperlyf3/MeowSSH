# Meowshell: the private-HOME check has no upgrade path, and no supported way to satisfy it

Written against `Sniperlyf3/meowshell@145cf28` (merge of #32,
"security-review-round-2"). Three findings.

The security change itself is right and should stay. What follows is about the
path into it, not the rule.

### How urgent this is

Finding 1 has two halves, and they have very different urgency:

- **A fresh install is fine.** `EnsureSecure` *creates* the directory at `0700`
  when it is absent, so a consumer that hands over a path and lets the library
  make it works correctly today.
- **An existing install is permanently broken.** A directory left at `0755` by
  an older version is rejected on every call, forever, with nothing in the
  library that repairs it.

So the priority depends entirely on whether anything using the affected code
paths has shipped to real users yet. If nothing has, this is a
fix-before-release item, not an emergency — and the right time to fix it is
precisely now, while no data is at stake and the repair is four lines.

MeowSSH itself is not blocked either way: it prepares the agent's HOME at
`0700` and narrows a pre-existing one, and it is not yet released. See the
closing section.

---

## Finding 1 — A HOME left at 0755 by an older version can never be used again

### What changed

`MeowshellAgentConnection.ConnectAsync` used to prepare its HOME like this:

```csharp
Directory.CreateDirectory(options.HomeDirectory);
```

It now validates instead:

```csharp
MeowshellHomeDirectory.EnsureSecure(options.HomeDirectory);
```

The same substitution was made at every other entry point, consistently:
`MeowshellServer.StartAsync` (both `WorkDirectory` and `HomeDirectory`),
`TailcatClient` (three call sites), `TailcatListener`,
`MeowshellPortForward`, and `MeowshellSocksProxy`.

`EnsureSecure` itself is not new — it is present in the released 0.1.474, and
this release in fact *relaxed* it, narrowing the rejected bits from "anything
outside `0700`" to the six group/other permission bits so that Android's
commonly-inherited `2700` is accepted. That relaxation is a fix and is welcome.

What is new is **where it is called**. In 0.1.474 the agent connect path still
did `Directory.CreateDirectory`; the validation applied only to the paths that
resolved their own default HOME. This release extends it to every entry point,
including `MeowshellAgentConnection.ConnectAsync` — the one a client app such as
MeowSSH uses for every connection it makes.

That is verifiable against the shipped package: MeowSSH's integration suite
passes today against 0.1.474 with a HOME created by a plain
`Directory.CreateDirectory` (mode `0755`), which both the old and the new
`EnsureSecure` would reject. It passes only because the agent path did not call
it. On upgrade, it will not.

`EnsureSecure` creates the directory at `0700` when it is **absent**, and
otherwise checks a **pre-existing** one:

```csharp
var mode = File.GetUnixFileMode(dir);
const UnixFileMode untrustedAccess =
    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
if ((mode & untrustedAccess) != 0)
    throw new IOException($"refusing to use {dir}: permissions {ToOctal(mode)} allow group/other access ...");
```

### Why that breaks existing installs

The old code created the directory with `Directory.CreateDirectory(string)`,
which applies the process umask to `0777`. Under the standard umask of `022`
that is **`0755`** — which has `OtherRead | OtherExecute` set, and is therefore
exactly what the new check rejects.

So on every machine that ran a previous version:

1. The old library created `HomeDirectory` at `0755`.
2. The user upgrades.
3. `EnsureSecure` takes the *pre-existing directory* branch, sees `0755`, and
   throws.
4. Nothing in the library ever narrows the mode, so step 3 repeats forever.

This is not a transient failure. There is no code path that repairs it, and
`EnsureSecure` runs before any process is spawned, so it fails on **every**
call to every entry point — connect, serve, forward, SOCKS.

Upstream's own tests confirm the rejected mode is the one the old code
produced. From `dotnet/Meowshell.Tests/MeowshellHomeDirectoryTests.cs`:

```csharp
[Fact]
public void EnsureSecureRejectsAPreExistingDirectoryReadableByOthers()
{
    // ...
    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);   // 0755

    var ex = Assert.Throws<IOException>(() => MeowshellHomeDirectory.EnsureSecure(target));
    Assert.Contains("group/other", ex.Message);
}
```

On Android this is unrecoverable by the user: the directory is inside the app
sandbox, and there is no shell to `chmod` it from. The only remedy available to
them is clearing app data, which on a host manager means losing every saved
host — for a directory the library itself created at the wrong mode.

### Why the caller cannot simply fix it themselves

`MeowshellHomeDirectory` is `internal`. A consumer that wants to hand over a
compliant HOME has to re-implement it:

- `Directory.CreateDirectory(path, UnixFileMode)` on Unix, plain
  `CreateDirectory` on Windows;
- the symlink / reparse-point rejection;
- the re-read-after-create, which exists because an unsafe parent can be raced;
- the Windows owner-and-ACL validation;
- and, to survive the upgrade, the narrowing of an existing directory.

That is a meaningful amount of security-sensitive code to duplicate, and a
consumer that gets it subtly wrong gets no signal. The library already contains
a correct implementation and declines to expose it.

### Second-order problem: the exception type is outside the documented model

`ConnectAsync` documents exactly one failure mode:

```csharp
/// <exception cref="TailcatException">The connection failed to establish within
/// <see cref="TailcatClientOptions.Timeout"/>; <see cref="TailcatException.Code"/>
/// names why when the agent reported a typed reason.</exception>
```

`IOException` is not in that list and is not a `TailcatException`. Every
consumer that follows the documentation — MeowSSH's engine does, catching
`TailcatException` and translating `TailcatException.Code` into typed
application errors — will not catch this. It escapes as an unhandled
exception from a call whose error contract says otherwise.

### The fix

Three parts, all small. They can land together.

**1. Narrow a pre-existing directory instead of only rejecting it.**

In `EnsureSecure`, when the directory exists and its mode grants group/other
access, attempt `File.SetUnixFileMode(dir, mode & ~untrustedAccess)` and
re-read. Throw only if it still grants access afterwards — which is the case
that actually matters: a directory someone *else* owns, where the chmod fails
with `UnauthorizedAccessException`.

This distinction is the point. A directory this library created at `0755` in a
location the caller owns is a bug in an older version of this library, and
repairing it is safe and correct. A directory owned by another user is an
attack and must still be refused. Rejecting both identically is what turns a
fixable situation into a permanently bricked install.

Note the ordering constraint: narrow the mode *before* concluding anything
about trust, but keep the symlink/reparse check first — never chmod through a
link.

**2. Make the helper public.**

Expose it as supported API, e.g.:

```csharp
namespace Meowshell;

public static class MeowshellHome
{
    /// <summary>Creates <paramref name="path"/> as a private, owner-only
    /// directory, or verifies and repairs a pre-existing one, and returns it.</summary>
    /// <exception cref="IOException">The path is a symlink, is a file, or is
    /// accessible to other users and cannot be narrowed.</exception>
    public static string Prepare(string path);
}
```

Callers that manage their own storage layout — an Android app choosing a child
of `FilesDir`, a service using a systemd `RuntimeDirectory` — then have one
supported call instead of a platform-conditional reimplementation.

**3. Document the exception, or wrap it.**

Either add `<exception cref="IOException">` to the `<exception>` list of every
entry point that now calls `EnsureSecure` (`MeowshellAgentConnection.ConnectAsync`,
`MeowshellServer.StartAsync`, `TailcatClient`'s three, `TailcatListener`,
`MeowshellPortForward`, `MeowshellSocksProxy`), or wrap it in a
`TailcatException` with a new `MeowshellErrorCode` such as `HomeDirectoryUnsafe`
so it stays inside the documented, typed error model.

Wrapping is the better option: the typed-code model is what makes these errors
presentable in a UI, and "your HOME is not private" is precisely the kind of
failure an app wants to explain in its own words rather than surface as a raw
`IOException` message containing a filesystem path.

### Tests to add

- `EnsureSecure` on a pre-existing `0755` directory **owned by the current
  user** narrows it to `0700` and returns, rather than throwing. This is the
  upgrade case and is the one that is currently broken.
- `EnsureSecure` on a directory whose mode cannot be narrowed (simulate by
  making the chmod fail, or by a directory owned by another uid where the test
  environment allows it) still throws.
- A symlink pointing at a `0755` directory is still rejected, and the target's
  mode is left untouched — proving the repair did not follow the link.
- `ConnectAsync` against an unsafe HOME surfaces the chosen documented type
  (`TailcatException` with the new code, if option 3's wrapping is taken).
- The existing `EnsureSecureRejectsAPreExistingDirectoryReadableByOthers` test
  needs updating rather than deleting: it should assert the *narrowing*
  behaviour for an owned directory, and a new test should cover the
  unnarrowable case it currently stands for.

---

## Finding 2 — `maxConnections` default changed from unlimited to 256, silently

`OpenLocalForwardAsync`, `OpenLocalForwardOnUnixSocketAsync`,
`OpenRemoteForwardAsync`, `OpenSocksForwardAsync` and
`OpenSocksForwardOnUnixSocketAsync` all changed their default:

```diff
-int maxConnections = 0,                            // 0 means unlimited
+int maxConnections = DefaultMaxForwardConnections, // 256
```

A bounded default is the right choice, and the new upper bound
(`maxConnections > 65_535` now throws) is a straightforward improvement. The
problem is only that this is a behavioural change invisible at the call site:
existing code that relied on the documented "zero (the default) means
unlimited" keeps compiling and silently acquires a cap. A forward carrying more
than 256 concurrent connections starts queueing in the listen backlog and then
refusing, which will present as an intermittent network fault rather than as a
limit anyone chose.

**Fix:** note it in the release notes and the XML doc as a breaking behavioural
change, since a caller reading only the signature cannot see it. No code change
needed — the new default is the better one.

---

## Finding 3 — Latent: the native-binary execute-bit repair can throw on Android

`MeowshellBinaries.EnsureExecutable` repairs a missing owner-execute bit:

```csharp
if ((mode & UnixFileMode.UserExecute) == 0)
    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
```

On Android the binaries live in `ApplicationInfo.NativeLibraryDir`, which is
owned by `system` and read-only to the app. `File.SetUnixFileMode` on a file the
process does not own throws `UnauthorizedAccessException`, which is neither
caught here nor documented.

This is **latent, not an active break**: the OS extracts native libraries at
`0755`, so the owner-execute bit is present and the branch is not taken. It
becomes real only if a device or packaging path ever produces a library without
it — at which point the failure is an unhandled `UnauthorizedAccessException`
rather than the more useful "this binary is not executable by me".

**Fix:** before attempting the chmod, check whether the file is already
executable *by this process* rather than by its owner — the app executes these
through the other-execute bit, not the user one. If it is, leave it alone. If it
is not, attempt the chmod inside a `try`/`catch` and, on failure, throw an
`IOException` that says the binary is not executable and cannot be made so,
naming the path.

---

## What MeowSSH did on its side

MeowSSH no longer depends on any of the above being fixed:

- Its engine prepares the agent's HOME itself, creating it `0700` and narrowing
  a pre-existing directory that grants group/other access
  (`MeowshellSshEngine.EnsurePrivateWorkingDirectory`).
- `ConnectAsync` now also catches `IOException` and turns it into the same
  typed `SshException` the rest of the connect path produces, so a refusal is
  something the UI can explain rather than an unhandled exception.

MeowSSH has not shipped, so it has no `0755` directories in the field and the
upgrade half of Finding 1 does not apply to it.

It is still worth fixing upstream, and cheaply fixed now rather than later:
any other consumer has the same problem, the repair logic does not belong
duplicated in each of them, and the documented-error-model gap remains
regardless of who creates the directory. Once something has shipped against
the affected version, the fix stops being four lines and starts needing a
migration.
