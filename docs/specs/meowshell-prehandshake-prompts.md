# Spec: let callers answer prompts raised during the handshake

**Repository:** `Sniperlyf3/meowshell`
**Affects:** `dotnet/Meowshell/MeowshellAgentConnection.cs`, `cmd/meowshell/agent.go`
**Status:** proposed
**Found by:** MeowSSH integration tests against a real OpenSSH server

## Summary

`MeowshellAgentConnection.ConnectAsync` completes the SSH handshake before it
returns, and its prompt hooks (`HostKeyPromptRequested`, `PasswordRequested`,
`PassphraseRequested`, `KeyboardInteractiveRequested`, `SignRequested`) are
instance events on the object it returns. There is therefore no instant at which
a caller can subscribe before those prompts are raised.

The practical effect is that **trust-on-first-use cannot be implemented against
this API**. A first connection to a host that is not already in `known_hosts`
always fails, because the agent asks whether to trust the key, nobody is
listening, and it declines.

## How it presents

An integration test connecting to a real sshd with an empty `known_hosts`:

```
Unknown: The connection failed.
host key prompts seen: 0
```

Two things are wrong there, and they share a cause.

1. **The prompt never reaches the caller.** Handlers are attached after
   `ConnectAsync` returns, by which point the handshake has already been decided.
2. **The failure is misclassified.** With no handler the agent declines the key,
   and the resulting "host key rejected" is not one of the cases
   `classifyConnectError` recognises, so it surfaces as `MeowshellErrorCode.Unknown`
   rather than `HostKeyUnknown`. A client cannot tell "this host is new" from
   "something else went wrong", which are very different things to show a user.

`HostKeyChanged` is classified correctly and does reach the caller, because that
path fails without asking anything. Verified against a real server:

```
HostKeyChanged: This server is presenting a different key than the one
MeowSSH recorded. Either it was rebuilt, or something is intercepting
the connection.
```

## Why this matters

Every SSH client has to answer "do you trust this host?" on a first connection.
OpenSSH prints the fingerprint and waits. Without it, the only ways to reach a
new host are to pre-populate `known_hosts` out of band — which means shipping an
`ssh-keyscan` equivalent and asking the user to trust a key the app fetched
itself, a materially weaker check — or to disable host key verification, which
is not an option.

This is the first thing a user does with the app, so it cannot be the one thing
the engine cannot do.

## Proposed change

### 1. A hook that exists before the handshake

Add an optional parameter carrying the handlers, so they are installed on the
connection before `SendConfigureAsync`:

```csharp
public static async Task<MeowshellAgentConnection> ConnectAsync(
    TailcatClientOptions options,
    string destination,
    MeowshellAgentConfigureOptions? configure = null,
    string? port = null,
    IReadOnlyList<string>? jumpHosts = null,
    string? knownHostsPath = null,
    string? proxyUrl = null,
    Action<MeowshellAgentConnection>? configureConnection = null,   // new
    CancellationToken cancellationToken = default)
```

`configureConnection` is invoked on the fresh instance immediately after
construction and before any protocol traffic, giving the caller exactly one
place to subscribe. The events themselves, their signatures, and everything
after the handshake stay as they are.

An `Action<T>` rather than a separate options record keeps this to one new
parameter and lets a caller attach every handler it wants without the library
growing a property per prompt.

### 2. Classify a declined or unanswerable host key

In `cmd/meowshell/agent.go`, the error returned when `tcpHostKeyCallback`'s
prompt declines should carry `errHostKeyUnknown` rather than falling through to
`errUnknown`. The distinction the client needs is:

| Situation | Code |
| --- | --- |
| Host absent from `known_hosts`; user declined or no handler | `host_key_unknown` |
| Host present with a different key | `host_key_changed` |

Only the first is a question. The second is a warning, and already behaves
correctly.

## Tests

- A connection to a host absent from `known_hosts`, with a handler attached via
  `configureConnection` that returns true, succeeds and appends the key to
  `known_hosts`.
- The same with a handler returning false fails with `host_key_unknown`.
- The same with no handler at all fails with `host_key_unknown` — not `unknown`.
- A password-authenticated connection succeeds with the password supplied by a
  handler attached via `configureConnection`, proving the hook covers every
  prompt raised during the handshake and not only the host key.
- The existing `host_key_changed` behaviour is unchanged.

## Out of scope

- Changing when the handshake happens. Completing it inside `ConnectAsync` is a
  good design; the only problem is that the caller cannot get a word in first.
- The prompt event signatures, which are fine as they are.

## Workaround until then

MeowSSH pre-seeds `known_hosts` in its integration harness with `ssh-keyscan`,
which is available on a test machine and is not a pattern the app can ship: it
trusts a key the tooling fetched rather than one a person confirmed.
