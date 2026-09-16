# Spec: metered relay access over a self-hosted DERP fleet

**Repositories:** `Sniperlyf3/MeowSSH` (app), the licensing service (does not yet exist), `Sniperlyf3/meowshell` (one blocking gap)
**Affects:** `src/MeowSSH.Core/Ssh/`, `src/MeowSSH.Core/Licensing/`, `src/MeowSSH.UI/`, `cmd/meowshell/agent.go`, `dotnet/Meowshell/MeowshellAgentConnection.cs`
**Status:** proposed
**Depends on:** the entitlement stack in PRs #32–#39, unmerged at the time of writing

## Why this exists

Tailcat's default relays belong to someone else. Upstream's terms are explicit:

> The public rate-limited Tailcat DERP relays have no uptime SLAs or throughput
> targets, and we may revoke access to them at any time, for any reason.

A paid product cannot rest on that, so MeowSSH runs its own DERP relays. That
creates two problems this spec solves together, because neither can be solved
without the other:

1. **An open relay is an open relay.** Anyone who learns the hostname can use it.
2. **Bandwidth we pay for has to be attributable**, or a tier limit is a wish.

The load-bearing observation is that *almost none* of this traffic is ours to
pay for. Tailcat bootstraps through DERP and then upgrades to a direct
peer-to-peer path; Tailscale reports direct NAT traversal succeeding "well north
of 90%" in typical conditions. Only the sessions where traversal fails relay
their payload, and only those that relay through *our* relays cost us anything.
So the system's real job is to distinguish four cases and charge for exactly one:

| Path | Relay | Costs us | Counts against quota |
| --- | --- | --- | --- |
| Direct | — | No | No |
| Relayed | MeowSSH relay | **Yes** | **Yes** |
| Relayed | User's own relay | No | No |
| Relayed | `tailcat.dev` default | No | No (but warn) |

Everything below follows from wanting that table to be true in practice.

## Vocabulary

- **Node key** — a tailcat identity's public key, `nodekey:<hex>`. Both ends of a
  tailcat connection have one; both are DERP clients; both need admitting.
- **Relay class** — whether an address's embedded DERP nodes are ours, the
  user's, or the public default. A property of the *address*, fixed when the
  address is minted.
- **Relayed byte** — a payload byte that traversed a MeowSSH relay. The only
  unit that costs money and the only unit metered.

---

# Part A — App changes

## A1. Classify an address by relay class

`TailcatClient.ParseAsync` already returns everything needed. A parsed address
carries either embedded DERP nodes (`Region[].Nodes[].HostName`) or a bare
`RegionId` that would be resolved against a DERP map.

Add to `MeowSSH.Core`:

```csharp
public enum RelayClass
{
    /// <summary>Direct-dial TCP or Tailscale SSH: no tailcat relay involved at all.</summary>
    NotApplicable,

    /// <summary>Every embedded DERP node is a MeowSSH relay. Relayed bytes are metered.</summary>
    MeowSsh,

    /// <summary>Embedded nodes the user runs themselves. Never metered.</summary>
    SelfHosted,

    /// <summary>No embedded nodes: resolves through tailcat.dev's map. Never metered.</summary>
    PublicDefault,

    /// <summary>Embeds both MeowSSH and foreign nodes. Refused at mint, metered if seen.</summary>
    Mixed,
}
```

Classification rule, in order:

1. Host transport is not `SshTransport.Tailcat` → `NotApplicable`.
2. `Region` is null or empty (address references a region by ID) → `PublicDefault`.
3. Every `Nodes[].HostName` is in the relay allowlist → `MeowSsh`.
4. No `HostName` is in the allowlist → `SelfHosted`.
5. Otherwise → `Mixed`.

The allowlist ships in the app as build-time configuration, next to the existing
`LicensingBuildConfig` metadata, not hardcoded — the relay hostnames will change
and an app pinned to dead hostnames is a bricked app.

Store the computed class on `HostRecord` as a new field. It is derived data, but
deriving it means shelling out to `tailcat parse`, which is not something to do
on every frame of the host list. Recompute on save and on import, not on read.

> **Mixed is refused, not handled.** When the editor computes `Mixed` for an
> address a user pasted, reject it with an explanation. A single address that can
> land on either our relay or a stranger's makes metering unanswerable, and no
> address MeowSSH mints will ever be shaped that way.

## A2. Mint addresses against our fleet

`TailcatKeyOptions.Region` accepts "one or more comma-separated hostnames". Mint
every MeowSSH-provided address against **all three** relays at once:

```csharp
var address = await TailcatClient.GenerateKeyAsync(options, new TailcatKeyOptions
{
    Name = workspaceKeyName,
    Region = string.Join(',', RelayConfig.Hostnames), // derp-eu / derp-us / derp-ap
    Psk = true,
});
```

Three things follow from this and all three are wanted: the client picks the
nearest by latency, no DERP map is ever fetched, and losing a region degrades
rather than breaks.

Offer a per-host "use my own relay" field that sets `Region` to the user's
hostname instead. That address classifies as `SelfHosted` and is never metered —
which is the honest outcome, and also a genuinely attractive Pro feature for the
kind of user who already runs Tailscale.

## A3. Register node keys

Both ends need admitting, so both node keys are registered.

**Client key.** The app's own identity. `TailcatClient.PrintPubAsync` returns the
saved client key's public half (`nodekey:…`), generating one first via
`GenerateKeyAsync` with `Client = true` if none exists. Register once per install
and re-register when the grant is refreshed.

**Server keys.** Every Workspace or phone-server address the app mints has a
server node key, available as `TailcatParsedAddress.ServerPublic`. Register at
mint time.

Registration is a call to the licensing service (Part B), authenticated by the
signed grant the app already holds. Nothing new is invented here — the existing
Play Integrity plus signed-grant flow from PR #34 is the credential.

> A key generated with the stock `tailcat` CLI outside MeowSSH will not be
> registered and will not be admitted. That is a deliberate consequence: "bring
> your own tailcat server" still works, but it has to use the user's own relay.
> Say so in the UI rather than letting it fail mysteriously.

## A4. Make the path visible

This is the part users feel. The same action costs one user nothing and another
user quota, for reasons entirely invisible to them, and that asymmetry generates
support tickets unless the app explains itself.

**Session header.** Extend the existing `StatusBadge` beyond
Connected/Reconnecting/Disconnected with a path indicator:

| State | Badge | Meaning |
| --- | --- | --- |
| Direct | `Direct` · ok | Peer-to-peer. Costs nothing, counts nothing. |
| Relayed, ours | `Relayed` · warn | Counting against the monthly allowance. |
| Relayed, theirs | `Relayed · your relay` · ok | Through the user's own DERP. |
| Relayed, public | `Relayed · public` · warn | tailcat.dev. No SLA; offer to switch. |
| Unknown | `Connecting` · neutral | Path not yet established or not reportable. |

Tapping it opens a short explanation. The wording that matters:

> Your network didn't allow a direct connection, so this session is going
> through a MeowSSH relay. Relayed traffic counts toward your monthly
> allowance; direct connections don't.

**Quota meter.** In Settings, with the tier's allowance, bytes used this period,
and the reset date. Warn at 80%, and again on the session screen when a transfer
would plausibly cross the line.

**Transfer confirmation.** Before an SFTP transfer on a relayed MeowSSH path,
show the size against the remaining allowance. Not a block — an informed choice.

## A5. Meter and report

Metering is an app-side ledger, flushed to the server.

Count a byte when **both** hold: the host's relay class is `MeowSsh`, **and**
the live path is relayed. Count the payload the app actually moved — shell
channel traffic plus SFTP transfer sizes — not an estimate.

Buffer locally and flush every 60 seconds and at session close, with an
idempotency key so a retry after a dropped response does not double-count:

```json
{
  "reportId": "01J8Z...",        // ULID, client-generated, unique per report
  "nodePublic": "nodekey:...",
  "period": "2026-09",
  "relayedBytes": 4194304,
  "observedFrom": "2026-09-16T10:00:00Z",
  "observedTo":   "2026-09-16T10:01:00Z"
}
```

> **This is client-reported and therefore spoofable.** That is an accepted risk,
> not an oversight. A successful spoofer gains bandwidth, not access to anyone's
> data, and the relays' own server-wide counters bound how much lying can happen
> in aggregate before it becomes visible. Part D describes the authoritative
> alternative if abuse ever justifies it.

**A5 is blocked on Part D.** The app cannot currently observe whether a live
session is relayed — see below. Until that lands, ship metering in observe-only
mode: record under the assumption that every `MeowSsh`-class session is relayed,
report it, display nothing, enforce nothing. The numbers will over-count by
roughly the direct-connection rate, which is exactly the measurement needed to
size the real allowances before they are promised to anyone.

---

# Part B — Licensing service

Three new responsibilities on the service that PR #34 already specifies. It does
not exist yet; this extends its contract rather than proposing a second service.

## B1. Schema

```sql
CREATE TABLE node_keys (
    node_public   TEXT PRIMARY KEY,          -- 'nodekey:<hex>', normalised lowercase
    user_id       TEXT NOT NULL REFERENCES users(id),
    kind          TEXT NOT NULL,             -- 'client' | 'server'
    label         TEXT,                      -- e.g. the workspace name, for the user's own UI
    registered_at TIMESTAMPTZ NOT NULL,
    last_seen_at  TIMESTAMPTZ,               -- updated by the admission controller
    revoked_at    TIMESTAMPTZ
);
CREATE INDEX ON node_keys (user_id) WHERE revoked_at IS NULL;

CREATE TABLE usage_periods (
    user_id       TEXT NOT NULL REFERENCES users(id),
    period        TEXT NOT NULL,             -- 'YYYY-MM', UTC
    relayed_bytes BIGINT NOT NULL DEFAULT 0,
    updated_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (user_id, period)
);

-- Append-only, for idempotency and for auditing a disputed bill.
CREATE TABLE usage_reports (
    report_id     TEXT PRIMARY KEY,          -- client-supplied ULID
    node_public   TEXT NOT NULL,
    user_id       TEXT NOT NULL,
    period        TEXT NOT NULL,
    relayed_bytes BIGINT NOT NULL,
    received_at   TIMESTAMPTZ NOT NULL
);
```

A node key is a durable link between a person and a network identity, and the
admission controller sees source IPs. Set a retention policy on `last_seen_at`
and on any admission logging before launch, and disclose both — MeowSSH's whole
pitch is that it cannot see your servers, and this is the one place that claim
needs qualifying honestly.

## B2. Registration

```
POST /v1/derp/nodes
Authorization: <the existing signed-grant credential>

{ "nodePublic": "nodekey:...", "kind": "server", "label": "build-01" }
→ 204
```

Fail closed on an absent, expired or invalid grant. A Free-tier user may
register keys — Free still gets a relay allowance — but the tier recorded
against the user determines the quota, and the tier comes from the grant, never
from the client.

`DELETE /v1/derp/nodes/{nodePublic}` sets `revoked_at`. Deleting a Workspace in
the app deletes its key here.

## B3. Admission controller

The endpoint `derper` calls. Its contract is fixed by upstream
(`tailcfg.DERPAdmitClientRequest` / `DERPAdmitClientResponse`) and is not
negotiable:

```
POST <verify-client-url>
{ "NodePublic": "nodekey:abc...", "Source": "203.0.113.9" }

→ 200 { "Allow": true }
```

Verified properties of the caller, from `derp/derpserver/derpserver.go`:

- Called **on every client connection**, synchronously, inside the DERP handshake.
- **5-second timeout** (`context.WithTimeout(ctx, 5*time.Second)`).
- Any non-200 is a refusal. The response body is read through a 4 KiB limit.
- **`derper` sends no authentication whatsoever** — no header, not even a
  content type. It is a bare `http.DefaultClient.Do` of a JSON POST.
- There is **no caching on derper's side**. None. Every connection is a round trip.

Four consequences, each of which is a requirement:

**Authenticate by URL secret and network.** Since `derper` cannot present a
credential, put the secret in the path — `-verify-client-url` takes a full URL —
and additionally restrict the endpoint to the three relay IPs. Rotate the path
secret by re-rolling the relays' flags.

```
--verify-client-url=https://api.meowssh.dev/v1/derp/admit/<32-byte-random>
```

**Cache hard.** An in-process cache keyed on node key, 60-second TTL, is enough
to turn per-connection lookups into near-zero database load. The correctness cost
is that a revocation takes up to a minute to bite, which is immaterial given
established connections are never re-checked anyway.

**Fail open.** Set `-verify-client-url-fail-open`. Fail-closed means an outage of
the licensing service is a total connectivity outage for every paying customer;
fail-open means an outage briefly leaves the relay usable by anyone who both
knows the hostname and happens to hit that window. The second failure is far
cheaper than the first. Alert on every fail-open event.

**Admission alone cannot stop a session in flight.** `DERPAdmitClientResponse`
carries only `Allow` — upstream's own comment reads `// TODO(bradfitz,maisem):
bandwidth limits, etc?` — so there is no "allow but throttle", and refusing
admission blocks *new* connections only. Established sessions continue until
they drop. Part E is how a runaway is actually stopped; admission is one half of
that loop, not a policy on its own.

When a user is over quota, answer `Allow: false` **and** invalidate their cached
entries (see Part E for why the ordering matters).

## B4. Usage ingest

```
POST /v1/usage/relayed
Authorization: <signed grant>

{ "reportId": "01J8Z...", "nodePublic": "...", "period": "2026-09",
  "relayedBytes": 4194304, "observedFrom": "...", "observedTo": "..." }
→ 200 { "periodBytes": 8388608, "allowanceBytes": 10737418240 }
```

Insert into `usage_reports`; on primary-key conflict, return the current totals
and change nothing. Only a successful insert increments `usage_periods`. Reject
a `nodePublic` that is not registered to the authenticated user, and reject a
`period` more than one month old.

`GET /v1/usage` returns the current period's totals for the app's meter.

---

# Part C — Relay deployment

Three stock `derper` instances. **No fork.** Everything this design needs is
upstream, and a forked relay on top of an already-vendored-and-patched tailcat
is a maintenance burden with no payoff.

| Region | Host | Suggested |
| --- | --- | --- |
| EU | `derp-eu.meowssh.dev` | Frankfurt |
| US | `derp-us.meowssh.dev` | Newark |
| APAC | `derp-ap.meowssh.dev` | Singapore |

```sh
derper \
  --hostname=derp-eu.meowssh.dev \
  --certmode=letsencrypt \
  --verify-client-url=https://api.meowssh.dev/v1/derp/admit/<secret> \
  --verify-client-url-fail-open \
  --mesh-psk-file=/etc/derper/mesh.psk \
  --rate-config=/etc/derper/rates.json
```

`--mesh-psk-file` is what makes Part E's eviction possible: it must hold 64
lowercase hex characters, identical across all three relays and the evictor.

`--rate-config` is a JSON file, reloaded on SIGHUP, giving every non-mesh client
a receive ceiling:

```json
{ "PerClientRateLimitBytesPerSec": 2097152, "PerClientRateBurstBytes": 4194304 }
```

The ceiling is global — the same for every client, not per user — so it is not a
tier mechanism. Its job is to bound how much bandwidth any single client can
burn inside the window between crossing a limit and being stopped.

> **The mesh PSK is the most powerful credential in this system.** A mesh peer
> bypasses the admission controller, is exempt from rate limiting, and can close
> any client on the relay. It is strictly more dangerous than the admission URL
> secret. Keep it on the relays and the evictor only, never in the app, and
> rotate it on its own schedule.

Operational notes, from `cmd/derper/README.md`:

- Open **TCP 80 and 443, UDP 3478**. Port 80 is required for Let's Encrypt.
- Only Let's Encrypt certs rotate automatically; any other cert needs a restart.
- **Do not** rate-limit UDP STUN packets, and rate-limit inbound TCP only, never
  outbound.
- **Do not** put a relay behind an HTTP proxy or a global load balancer, and do
  not run several DERP nodes in one region.
- Upstream warns plainly that running DERP "requires expertise in multi-layer
  network and application diagnostics". Budget for learning it.

`--verify-clients` (no URL) is the wrong flag — it needs a local `tailscaled`
and a tailnet ACL, which tailcat, having no control plane, cannot provide.

Sizing: one small instance per region is ample. Community reports put ~50
concurrent peers under 100 MB RAM at negligible CPU; bandwidth is the only
variable that scales. Prefer a provider that pools transfer across regions so an
idle region's allowance absorbs a busy one's overage.

---

# Part D — The Meowshell gap that blocks A4 and A5

**The app cannot currently tell whether a live session is relayed.** This is not
a MeowSSH oversight; the information does not cross the agent protocol.

Verified:

- `cmd/meowshell/protocol.go`'s `controlMessage` has no transport, path, endpoint
  or byte-count field. Its messages cover channels, prompts, auth and errors —
  nothing about how the connection is carried.
- `MeowshellAgentConnection` exposes no path information.
- `TailcatClient.PingAsync` *does* report it — `TailcatPong.Direct` and `.Via`,
  parsed from tailcat's `pong in 42.1ms via DERP(sfo)` — but `PingAsync` spawns a
  **separate one-shot `tailcat` process** that establishes its **own** connection.
  Its path is evidence about the network, not about the agent's live session.

## What is needed

A new agent control message, pushed on the existing frame protocol, reporting
the session's current path and updating when it changes:

```json
{
  "msg": "path",
  "direct": false,
  "via": "derp-eu.meowssh.dev",
  "relayed_bytes_sent": 182400,
  "relayed_bytes_recv": 906112
}
```

- Emitted once when the connection is established, again on every
  direct↔relayed transition, and periodically (30 s) while relayed.
- `via` should be the relay **hostname** where one is known, not only the region
  code. The app has to compare it against an allowlist of hostnames, and a code
  like `sfo` cannot be matched against `derp-us.meowssh.dev` without a lookup
  table the app has no way to build.
- The byte counters make metering authoritative rather than inferred. They are
  the single highest-value part of this message: with them, A5's client-reported
  ledger stops being a guess.
- Surfaced in .NET as an event on `MeowshellAgentConnection`, e.g.
  `event Action<MeowshellPathInfo>? PathChanged`.

magicsock already knows all of this internally; the work is plumbing it out
through the agent, not computing it.

## Until it lands

Ship A5 in observe-only mode per its own note: assume relayed on every
`MeowSsh`-class session, record, report, display nothing, enforce nothing. The
over-count is itself the measurement that sizes the real allowances.

Do **not** substitute a periodic `PingAsync` for this. It doubles connection
setup, reports a different connection's path, and would be wrong in exactly the
cases that matter most — a phone whose agent relayed but whose fresh ping
happened to punch through.

---

# Part E — Stopping a runaway

Metering that only reports is a bill, not a limit. This is how a user who has
reached their allowance is actually stopped, and how far past the line they can
get before it happens.

Both mechanisms are stock upstream. Still no fork.

## E1. Evicting an established client

`derper` implements a privileged DERP frame for exactly this, and the Go client
library exposes it:

```go
// derp/derp_client.go:381
// ClosePeer asks the server to close target's TCP connection.
func (c *Client) ClosePeer(target key.NodePublic) error
```

Server side (`derp/derpserver/derpserver.go:1234`), `handleFrameClosePeer`
refuses unless `c.canMesh`, which `isMeshPeer` grants on a constant-time
comparison of the client's mesh key against the server's. Given permission it
closes every connection held for that node key:

```go
set.ForeachClient(func(target *sclient) {
    go target.nc.Close()
})
```

So the evictor is a small service that holds the mesh PSK, maintains a mesh
connection to each of the three relays, and calls `ClosePeer` on demand. Mesh
peers skip admission (`verifyClient` returns early for them), so the evictor
needs no registration.

## E2. The loop

Eviction on its own accomplishes nothing — the client reconnects within
seconds. It only works paired with admission, **in this order**:

1. A usage flush crosses the allowance. Mark the user over-quota.
2. **Invalidate the admission cache for every node key they own.** Skipping this
   leaves up to a full TTL during which reconnects are still admitted, which
   turns eviction into a free session refresh.
3. The admission controller now answers `Allow: false` for those keys.
4. The evictor sends `ClosePeer` for each key, on all three relays.
5. Reconnect attempts bounce off admission.

Evict both ends — client key and server key. Closing either stops the flow,
since a relay only relays between two connected peers, but leaving one half
connected wastes a slot and confuses the metrics.

## E3. How far over they can get

The overage ceiling is:

```
(flush interval + cache invalidation + eviction latency) × per-client rate cap
```

With a 60-second flush and the 2 MB/s cap from Part C, worst case is roughly
120 MB past the line. Shorten the flush interval as the limit approaches — every
10 seconds above 90% of the allowance — and it drops to about 20 MB.

That is the answer to "prevent them going far over": the rate cap bounds the
blast radius, and the eviction closes it. Neither does the job alone.

## E4. Ask before you take

**DERP has no channel to explain a disconnection.** An evicted client sees a
dropped connection, indistinguishable from a tunnel failure, with no reason
attached. If eviction is the primary mechanism, every over-quota user gets a
mystery outage and files a bug.

So the client is given the chance to comply first:

1. App warns at 80%, in the session UI and in Settings.
2. The flush response already returns `periodBytes` and `allowanceBytes`, so the
   app learns it is over on its next flush — no extra round trip.
3. **The app closes the session itself**, showing why, and offers the upgrade.
4. The evictor fires only if the client is still connected ~30 seconds later.

Honest clients get a clear message and a clean shutdown. Modified clients get
closed anyway. Eviction is the backstop, not the front line — and because it
only runs against clients that ignored a graceful request, an eviction in the
logs is itself a useful abuse signal.

---

# Rollout

1. **Relays up, admission fail-open, nothing enforced.** Registration live,
   admission returning `Allow: true` for every registered key. Set the Part C
   rate cap from day one — it costs nothing when nobody is near a limit, and it
   is the only thing standing between a bug and a bandwidth bill.
2. **Observe-only metering.** A5 without the UI. Collect a month of over-counted
   data and learn the real relay rate.
3. **Part D lands in Meowshell.** Metering becomes accurate; A4's badges go live.
4. **Allowances published.** Sized from step 2's data, not from a guess, with the
   meter visible before any limit is enforced.
5. **Graceful enforcement.** E4's first three steps only: warn, then have the app
   close its own session with an explanation. No eviction yet.
6. **Eviction armed.** E1–E3. Deploy the evictor, and only then turn on
   admission refusal — in that order, or a refused reconnect is the first thing
   an over-quota user meets, with no explanation attached.

## Decisions still open

- **Free-tier allowance.** Non-zero, or Free is direct-connections-only? A Free
  user behind CGNAT would find the tailcat features simply broken, which is a
  worse first impression than a small allowance that runs out.
- **Whether `PublicDefault` addresses stay supported at all.** They cost nothing
  and work today, but they depend on relays that upstream may revoke without
  notice, and a user whose workspace dies that way will blame MeowSSH.
- **Retention** for `last_seen_at` and admission source IPs.
