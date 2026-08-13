# ADR-0009: Collector numbers and set sizes join from a pinned TCGdex mirror

**Date:** 2026-08-13
**Status:** Proposed — built to the recommended defaults; the decisions marked *provisional* below await an owner ruling and are cheap to reverse before anything depends on them.

## Context

The web application this scraper feeds renders a card subline of the form
`{set name} · 215/203`. Two fields in it exist nowhere in this schema: the
**collector number** (today embedded in `cards.name` — "Umbreon VMAX #215" —
and parsed out at render as a stopgap) and the **official set size** (the
`/203` denominator; secret cards are numbered past it, so `215/203` is
normal). The owner directed that this enrichment be built here, in the batch
repo, not in the consuming app (CardStock D-079, 2026-08-12).

**Scope is deliberately narrow.** The owner removed species from this
enrichment ("in another phase, we will have to create a Pokédex, and it will
belong in there") even though the same API response carries it free — so no
`dexId`, no species columns, no number→name resolution. Images were separately
evaluated and rejected (`DATA_MODEL.md` §2): TCGdex keeps one image per card,
which cannot distinguish a holo from a non-holo printing. This ADR adds two
metadata fields and a match status. Nothing else.

**Source.** TCGdex (tcgdex.dev) answers both fields directly:
`localId: "215"`, `set.cardCount: { official: 203, total: 237 }` — re-verified
live 2026-08-13 (12 probes, all as expected). Its data lives in
`tcgdex/cards-database` under plain MIT: permanent Postgres storage,
modification, and commercial use expressly permitted; the only obligation is
notice preservation, and MIT covers their compilation, not Pokémon IP — the
same non-affiliation posture this project already carries. The alternative,
pokemontcg.io, was rejected for permanent columns: its bulk-data repo has no
license at all (`license: null`, no LICENSE file), its ToS is silent on
storing data while making access terminable at will, and its live API
returned 5xx on 8 of 10 probes during evaluation (2026-08-12).

**The corpus, measured against production on 2026-08-13** (read-only audit,
91,596 active cards in 789 sets):

| partition (by set slug) | sets | active cards | with `#number` |
|---|---|---|---|
| english | 222 | 42,855 | 40,711 |
| japanese | 395 | 34,299 | 31,582 |
| korean | 53 | 6,200 | 6,125 |
| chinese | 86 | 5,524 | 5,456 |
| topps | 33 | 2,718 | 2,668 |

TCGdex's `/en` catalog holds 218 sets (probed 2026-08-13) — including TCG
Pocket's *digital* sets ("Genetic Apex", "Eevee Grove"), which must never
match physical products — and serves Japanese sets only under its `ja`
locale with Japanese-script names (`/en/sets/S6a` → 404). So the non-English
partitions cannot auto-join, and 53% of the corpus is out of scope for this
phase by construction.

**The join is number-driven, with the name as a confirmation gate.** The
research session's executed join matched 283/283 numbered products across two
sampled pages with ~97% exact name agreement; every disagreement fell in
known synonym classes (Electric/Lightning Energy, Dark/Darkness, Steel/Metal,
gender symbols, accents, VSTAR casing). Name alone can never be the key —
Evolving Skies holds two distinct "Umbreon VMAX" (95 and 215). Number alone
must never be trusted — Celebrations files Classic Collection reprints under
their original numbers, so "Charizard #4" lands on Celebrations #4, which is
Palkia; the name gate turns that into an honest mismatch instead of a
silently wrong denominator.

Exact-name matching after normalization (accent/case fold, ampersand↔"and",
leading "Pokemon" stripped — and nothing fuzzier) maps 138 of the 222 English
sets, covering 36,288 of 42,855 English cards, with zero collisions in either
direction — measured against production names and the live catalog. A
~29-row hand-alias file covers the bridgeable rest (151, Expedition Base Set,
McDonald's Collection years, trainer-kit half-deck pairs). What remains
unmatched has no TCGdex counterpart at all: World Championships decks,
Trick-or-Trade, TCG Classic decks, merchandise lines (Dunkin, Artbox, Danone,
KFC…). Those stay unmapped, honestly.

## Decision

**1. A ninth table, `tcgdex_enrichments`, append-only and change-only.**
One row per verdict that differed from the card's latest, PK
`(card_id, computed_at)` — the same construction that makes `price_months`
and `populations` append-only structurally rather than by convention, and
asserted the same way in `SchemaModelTests`. The latest row per card is the
current verdict; a card with no rows has never been attempted, so the
consumer can distinguish "no match" from "not yet tried". Columns:
`match_status`, `card_number` (text — localIds carry prefixes and meaningful
leading zeros), `set_official_size` (null when TCGdex publishes 0; a zero
denominator is a lie), `tcgdex_set_id`, `tcgdex_card_id` (deliberately N:1
across PriceCharting's variant products), `tcgdex_name` (confirmation record
/ mismatch evidence), `tcgdex_version` (provenance: which mirror produced the
verdict). *Provisional:* the append-only shape over a mutable
one-row-per-card upsert; the table name.

**2. Six verdicts, and guessing is never one of them.**
`Confirmed` (number hit, name agreed — fields written) · `NameMismatch`
(number hit, name refused; evidence recorded, nothing written) ·
`NumberNotFound` · `Ambiguous` (multiple candidates agreed — bare-numbered
promos collide across eras) · `NoNumber` (nothing to join on: sealed product,
but also genuine unnumbered cards like the Unown [A]–[Z] run — excluded from
every coverage denominator) · `UnmappedSet`.

**3. Two-phase join.** Phase A maps sets: partition by slug prefix
(non-English and Topps are excluded *before any comparison runs* — fuzzy
matching would happily enrich "Pokemon Korean Scarlet & Violet 151" from
TCGdex's English "151"), then curated aliases (`tcgdex-set-aliases.json`,
repo root, same user-input posture as `blacklist.json`), then exact
normalized-name equality. Phase B joins cards: parse `#number` from
`cards.name`, canonicalize (uppercase, leading zeros dropped per digit run:
"053"≡"53", "TG04"≡"TG4"), route by alpha prefix — TG/GG/SV/CC gallery and
vault siblings, era-prefixed promos to their per-era promo set, the
`pokemon-promo` grab-bag across all of them — then confirm by folded name
with the measured synonym classes unified. Routed siblings are searched
before the primary set, because the prefix names the sub-catalog whose
denominator is the printed one.

**4. A pinned local mirror, never a live dependency.** The enrichment lane
fetches TCGdex's ~219 per-set JSON documents once — spaced 1 s apart, contact
address in the User-Agent, a different host outside the politeness gate like
the image CDN — into `TcgdexMirrorDirectory`, manifest written last. The
directory IS the pin: every sweep joins against disk, re-runs are
reproducible, and TCGdex's uptime cannot touch the crawl. Refresh is an
operator action: delete the directory, the next sweep re-fetches.
*Provisional:* delete-to-refresh as the re-pin mechanism. The manifest
records fetch date and, best-effort, the `cards-database` release tag
(v2.47.0 at time of writing) as `tcgdex_version`.

**5. A seventh lane.** `EnrichmentLane`, daily by default, entirely local
after the mirror exists: load catalog + aliases, resolve the set map, compute
all verdicts in memory, append only what changed. A sweep over unchanged
inputs writes zero rows. Strictness inherited from the parsers: a mirror
document missing a field the join computes from refuses the sweep loudly
rather than enriching from a guess.

**6. No new grants.** `pokemon_app` already receives SELECT + INSERT on
future tables via default privileges; append-only needs nothing else — no
UPDATE (the fourth mutable table that would otherwise exist), no DELETE
anywhere, and the store's "none of which has ever been updated in place"
stays true.

## Alternatives considered

**pokemontcg.io as the source.** Carries both fields (`number`,
`printedTotal`) and is current. Rejected on licensing (unlicensed bulk repo,
ToS silent on storage, revocable at will) and demonstrated 5xx instability —
the wrong foundation for permanent columns.

**Live TCGdex calls, no mirror.** ~92k requests per full pass against a
courtesy API that asks for local caching, an uptime coupling this worker
never needed, and unpinned data shifting under re-runs. Rejected.

**Self-hosting tcgdex/server (Docker, linux/arm64).** Works, but stands up
infrastructure on the Pi to serve ~10 MB of JSON this code can read from
disk. Reconsider if a future phase needs the query API.

**Vendoring the cards-database repo.** Freshest data, but TypeScript sources
with the card number encoded in file names with era-dependent zero-padding —
integration friction with no payoff over the API's JSON.

**A mutable one-row-per-card table (upsert).** Simpler reads for the
consumer. Rejected: it adds a fourth UPDATE grant, breaks the store's
never-updated-in-place property, and discards the verdict trail that makes a
bad join auditable after a mirror bump. The consumer already reads
latest-per-key everywhere else in this schema.

**Fuzzy set-name matching.** Would bridge most of the alias file
automatically — and silently enriched Korean cards with English-set data on
its first hypothetical outing. Rejected outright; the alias file is the
entire fuzziness budget, and it is reviewed by a human.

**Enrichment columns on `cards`.** Mixes another source's derived data into
the crawl's catalog row, with no room for per-source provenance or the
verdict trail, and couples a future enrichment re-run to the scheduler's
hottest table. Rejected.

## Consequences

**Good:**
- The consumer swaps its render-time name parse for two real columns plus an
  explicit per-card status it can render honestly, and can tell "no match"
  from "not yet attempted" by row presence.
- Measured, authored coverage (2026-08-13 audit over the full production
  corpus — 91,644 cards, active and delisted, not-a-card excluded — against
  a fresh v2.47.0 mirror and the repo's alias file; reproducible via
  `TcgdexCoverageAuditTests`): **37,743 cards Confirmed — 41.2% of the whole
  corpus, 92.7% of the 40,723 numbered English cards.** NameMismatch (1,014)
  plus Ambiguous (9) are 2.5% of numbered English, and the review sample is
  the gate working: Jumbo/Staff promo variants colliding with SVP numbers,
  Celebrations' renumbered reprints, plus a shortlist of real synonym
  classes (M/Mega EX, LV.X suffixes, "Team Magma's" prefixes,
  subtitle-after-colon) for future owner-reviewed whitelisting.
  NumberNotFound is only 232, a third of it the trainer-kit half-deck
  pairs whose PC numbering runs past each half's 1–30.
- Re-runs are idempotent and cheap; the verdict history is auditable with
  per-row provenance; a mirror bump re-verdicts only what actually changed.
- Zero grant changes, zero migration risk to existing tables, zero coupling
  of the crawl to a third-party API.
- This is the first enrichment, not the last (owner, 2026-08-12). The
  pattern it sets — a per-source table keyed by the PriceCharting product id,
  an explicit match status where unmatched is first-class, provenance on
  every row, a pinned snapshot of the source — is recorded here as the
  expectation for the next one. Deliberately *not* built: any generalized
  "enrichment framework"; both repos' rule is that expectations get recorded,
  not speculatively built.

**Costs:**
- The non-English 53% of the corpus is `UnmappedSet` by construction. Japanese
  (34,299 cards, the largest single block) needs a curated ~395-row alias
  table against TCGdex's `ja` locale — a deliberate later phase, not a fuzzy
  matcher.
- The pin means new sets reach PriceCharting weeks before the mirror learns
  them; their cards sit `NumberNotFound`/`UnmappedSet` until the operator
  re-pins. That is the honest state, and the audit's per-set
  `NumberNotFound` table is the re-pin signal.
- The consumer must read latest-per-card (`DISTINCT ON (card_id) … ORDER BY
  card_id, computed_at DESC`) — the same discipline every other history table
  here already demands.
- ~92k verdicts recompute in memory each sweep to decide that nothing
  changed. Measured cost is seconds; accepted for the simplicity of having no
  incremental bookkeeping to corrupt.
- `cards.set_id` staleness (the documented back-burner TODO) routes a moved
  card's join through its old set until fixed; the name gate downgrades the
  damage from wrong-data to no-data.
