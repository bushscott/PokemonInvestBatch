# ADR-0006: A localhost intake API, with express visits outside the polite gate

**Date:** 2026-08-09
**Status:** Accepted

## Context

The product this scraper feeds is a trading website. Its web application — and any other sibling
app — will live on the same Raspberry Pi, read the same Postgres, and sometimes need a card's
data refreshed ahead of its normal turn. Two distinct urgencies emerged in design:

1. **"Refresh this card soon"** — fire-and-forget. The card should take the next crawl slot,
   *unless* that slot belongs to a burn-window-due card; the zero-missed-sales guarantee outranks
   any ask.
2. **"Refresh this card now, and tell me when it's done"** — a caller actively waiting, wanting
   the visit to happen immediately and the response to confirm completion.

Until now the worker had no inbound surface at all: data flowed one way, site → parsers →
Postgres, and the only external inputs were config, `blacklist.json`, and the operator's own SQL
(ADR-0002). Whatever channel carries these asks must not hand other apps write access to the
scraper's tables — the ownership rule (each codebase migrates and writes only its own tables)
predates this decision and survives it.

## Decision

**Host a minimal HTTP API inside the existing worker process, bound to 127.0.0.1 only.** Kestrel
rides along via `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; the host becomes
`WebApplication.CreateBuilder`, every lane unchanged. Trust comes from the bind address: only
processes on this machine can reach it, which is exactly the trust model the callers have already
been granted by living here. No auth, no TLS, no port exposed off-box.

Two endpoints, keyed by card id — the one name both sides already share through the database:

- **`POST /cards/{id}/refresh-request`** stamps `cards.refresh_requested_at` and returns 202.
  The scheduler serves it at a new tier (2,750,000) strictly between burn-window-due (3,000,000)
  and never-visited (2,500,000): the next slot, unless prevention owns it. The ask is cleared by
  the next successful visit from either path, or by the not-a-card verdict; failed visits leave
  it standing, so it survives quarantine. Re-filing keeps the original timestamp — the pool
  serves oldest ask first. 404 unknown card; 409 delisted or not-a-card (the scheduler would
  never serve them, so accepting would be a lie); benched cards are accepted with their comeback
  date in the receipt.

- **`POST /cards/{id}/express-visit`** runs the visit immediately — no scheduler, **no polite
  gate** — and holds the response open until the visit commits, returning the outcome (200
  parsed / 502 upstream error / 422 refused page / 504 timeout). The user chose gate bypass
  explicitly: the caller is a human-facing app, and "instantaneous" was the requirement.

An express visit is a full visit. It runs the same `CardVisitor` pipeline as the lane, so
`last_visited_at` resets (the regular schedule immediately sees the card as fresh), history rows
land identically, strikes and quarantine are earned identically, failures feed the same AIMD
backoff, and a pending refresh ask is satisfied by it. Express and lane visits differ only in how
the card was chosen, the gate, and the metrics lane tag (`express`).

**Express guardrails**, in place of the gate it skips:

- **Single-flight**: at most one express visit in flight, ever.
- **Spacing floor** between consecutive express fetches (default 10 s, the AIMD floor).
- **Same-card coalescing**: concurrent requests for one card share one fetch — a double-clicked
  refresh button costs the site nothing extra. A caller's disconnect abandons its await, never
  the visit; the visit runs on the worker's lifetime plus a timeout, so a coalesced waiter is
  never orphaned by someone else hanging up.
- **`PoliteGate.RecordFetchNow()`**: the express fetch stamps the gate's last-fetch instant
  without taking a turn, so the scheduled lane re-spaces around it. Express never waits; the
  lane absorbs the spacing, and the site's view of our cadence stays whole.

Express visits are allowed on delisted cards (they are the operator's "is it back?" probe in
synchronous form — the visit writes append-only history while `delisted_at` stays untouched,
ADR-0002 intact) and on benched cards (any success clears the bench). They are refused only for
not-a-card verdicts, where a revisit is pure waste and would re-raise a settled alert.

## Rejected

- **A Postgres queue table** other apps INSERT into. No new listener, but it hands sibling apps
  a write path into the scraper's database — the ownership line this project deliberately drew —
  and the synchronous mode degrades into caller-side polling.
- **A Unix domain socket.** Same semantics as loopback TCP with marginally tighter permissions,
  but every caller needs custom HttpClient wiring and casual `curl` debugging over SSH gets
  harder. On a single-user Pi the loopback bind buys the same isolation.
- **A separate API process.** A second deployment unit, systemd unit, and DB connection for zero
  isolation gain — the worker is the thing that owns visits, so the worker answers for them.
- **Express through the gate.** Considered and explicitly declined by the user: during AIMD
  backoff the gate stretches to minutes, and the express caller is a human-facing feature. The
  guardrails above are the price; the pause counters still learn from every express failure.

## Consequences

- The self-contained linux-arm64 publish grows ~30 MB (the ASP.NET Core shared framework).
- `DATA_MODEL.md`'s "no HTTP API of its own" is amended: there is now one inbound surface, it
  serves no market data, and data still flows one way from the site.
- While the three-strike pause is in force, an express request can still poke the site once per
  spacing floor. Deliberate: express failures feed the same counters, and the caller is trusted.
  If it ever becomes a problem, a "refuse express during the pause" toggle is the follow-up.
- A card can be double-fetched when the lane and an express visit race on the same card. Both
  paths are single-transaction and sales dedup on `(source, source_id)`, so the worst case is a
  redundant change-only row at a second `observed_at` — append-only absorbs it.
- Worst-case extra site load is bounded: one request per spacing floor, operator-initiated.
- No systemd change (same binary, same unit), no firewall or `pg_hba.conf` change (loopback
  never leaves the box), no new DB grants (`pokemon_app` already holds UPDATE on `cards`;
  sibling apps speak HTTP to the worker, never SQL to its tables).
