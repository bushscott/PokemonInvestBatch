# ADR-0008: Express visits have no time barriers; the calling app owns the rate limit

**Date:** 2026-08-10
**Status:** Accepted — supersedes the "Express guardrails" of
[ADR-0006](0006-localhost-intake-api-and-express-visits.md) except same-card coalescing and
`RecordFetchNow()`; the rest of 0006 stands

## Context

ADR-0006 fenced `POST /cards/{id}/express-visit` with three waits, standing in for the polite
gate it skips: a 10-second floor between express fetches, single-flight (one express visit in
flight, ever), and a 120-second timeout that answered 504. They were sized for the caller 0006
imagined — an operator poking one card at a time.

The real caller is the trading website, and the endpoint exists so a human-facing page can show
current data *now*. Every wait defeats that:

- **The floor was global** — not per user, not per card. One visitor browsing several stale cards
  waited ~10 s for the second page, ~20 s for the third, and every other visitor queued behind
  them.
- **Single-flight is the same defect without a timer.** Two people refreshing two different cards
  still take turns: the second person waits out the first person's entire fetch before theirs
  begins.
- **Neither wait is visible to the caller.** The request simply hangs; nothing distinguishes "we
  are fetching" from "you are in a queue you cannot see".
- The calls are already scarce by construction. The consumer app reads `cards.last_visited_at`
  and calls express only when the card is more than 24 h stale, so an express call means a real
  user is looking at a real page the schedule has not reached.

The worker is also the wrong place to hold a limit. It sees a card id and nothing else — not who
asked, not how many pages that person has opened. The calling app knows both.

## Decision

**An express visit starts its fetch the moment it is asked, and waits for nothing.** Removed:
the spacing floor (`Scraper:ExpressSpacingSeconds`), single-flight, and the express timeout
(`Scraper:ExpressTimeoutSeconds`) with its 504 response, along with their startup validation and
the runner's last-fetch bookkeeping. Express visits for different cards now run in parallel.

**One ask, one fetch. A failure is reported, never retried.** There is no retry anywhere on this
path and none is added: the visit fetches once and answers with what happened. Unexpected
failures now carry the exception — type, message, and the innermost provider message — in the 500
body, instead of the previous `"unexpected failure; see the worker log"`. The caller is a page
that has to say something to a person; a masked error is not something it can act on.

Kept from 0006, deliberately:

- **Same-card coalescing** — concurrent asks for one card share one fetch. It shares work rather
  than imposing a wait: the second caller waits exactly as long as its own fetch would have taken,
  and a double-clicked refresh button still costs the site one page.
- **`PoliteGate.RecordFetchNow()`** — the scheduled lane still re-spaces around every express
  fetch, so the *combined* cadence the site sees stays whole.
- **The same `CardVisitor` pipeline** — `last_visited_at` still resets, history rows, strikes,
  quarantine, and AIMD backoff are still earned identically.
- **The polite gate itself**, for the normal crawl lane. Untouched.

**Rate limiting moves to the calling app.** It knows the identity behind each request and can
throttle per user, per session, or per page. The worker no longer bounds express volume at all.

## Rejected

- **A per-user or per-session limit in the worker.** The worker would have to be told who is
  asking — a new field on the route, trusted blindly over loopback, duplicating state the caller
  already holds. The limit belongs where the identity is.
- **A shorter floor, or a concurrency cap above one.** Still global, still queues a visitor behind
  strangers, still invisible to the caller. A smaller version of the same defect.
- **A token bucket in the worker** (burst of N, refill per minute). Bounds load without punishing
  a single browsing user, but it is the calling app's policy expressed in the wrong process, and
  the first "why did my page hang" report leads to a bucket nobody can attribute.
- **Retrying a failed express visit inside the worker.** The caller is synchronous and waiting; a
  retry spends their patience on a second guess instead of telling them what happened. The page
  can ask again — that decision belongs to whoever is watching the spinner.
- **Keeping the express timeout.** `HttpClient` already caps a fetch at 60 s and reports a dead or
  hung site as an ordinary HTTP failure (status 0 → 502), which feeds the same backoff. A second
  deadline on top of that only added a response code that meant "we gave up on our own wait".

## Consequences

- **ADR-0006's "Worst-case extra site load is bounded: one request per spacing floor,
  operator-initiated" is no longer true, and neither is its single-flight promise that express
  "never opens parallel connections".** The ceiling is now whatever the calling app sends: express
  fetches run concurrently and unbounded. That is the honest new bound, and the worker enforces
  nothing below it.
- The consumer app must throttle express calls per user before shipping user-facing refresh. Its
  existing 24 h staleness check limits *which* cards qualify, not *how fast* one visitor can ask.
- **An existing write race had to be closed to make this safe.** `PageFingerprintArchive` read a
  fingerprint with `FindAsync` and inserted on a miss, so two visits meeting the *same brand-new*
  page shape both inserted and one died on `pk_fingerprints` — inside `CardPageWriter`'s
  transaction, so the whole visit rolled back and the caller got a 500 it had done nothing to
  earn. This predates express concurrency: the crawl lane and an express visit could already
  collide, since single-flight never serialized express against the lane and both write through
  `CardVisitor`. Dropping single-flight simply added express-vs-express. It was never about new
  *cards* — a new card laid out like every other one hashes to a shape already on file. The
  archive now claims the row with a single `INSERT ... ON CONFLICT (hash) DO UPDATE ... RETURNING
  xmax = 0`, executed on its own outside the caller's transaction: both visits succeed, exactly
  one archives the HTML and considers the alert. Consequences of that: the fingerprint row commits
  independently of the visit that saw it (which matches the archived HTML file, never
  transactional), and `last_seen_at` no longer holds a row lock on one of two hot rows for the
  duration of every page write — a bottleneck that only mattered once express went parallel.
- 0006's "an express request can still poke the site once per spacing floor" during the
  three-strike pause becomes "as fast as express calls arrive". Express failures still feed the
  pause counters; if a stuck site plus an eager caller ever becomes real, the follow-up is the
  toggle 0006 already named — refuse express during the pause — not a new wait.
- The lane still absorbs express fetches through `RecordFetchNow()`, so a burst of express visits
  shows up as the scheduled crawl going quiet, not as two crawlers talking over each other.
- 504 leaves the express contract; `crawl.express_visits` loses its `timeout` outcome tag. Any
  dashboard or alert filtering on either sees them stop rather than break.
- No config change on the Pi: neither `Scraper:ExpressSpacingSeconds` nor
  `Scraper:ExpressTimeoutSeconds` was ever set in an `appsettings*.json`, so removing both options
  touches no deployed file.
