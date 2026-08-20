# ADR-0012: The Japanese shelf joins by hand, and the mirror learns to top up

**Date:** 2026-08-19
**Status:** Accepted

## Context

ADR-0009 built the TCGdex join for the English shelf and deliberately walled every
other partition off before any comparison runs: TCGdex serves Japanese sets only
under its `ja` locale with Japanese-script names, and the name normalizer
(`NameFold`) folds non-ASCII to nothing, so a name-driven join across scripts is not
merely unreliable — two Japanese names both fold to the empty string and would
compare *equal*, confirming anything against anything. ADR-0009 recorded the
sanctioned way forward in its own consequences: *"Japanese (34,299 cards, the
largest single block) needs a curated ~395-row alias table against TCGdex's `ja`
locale — a deliberate later phase, not a fuzzy matcher."*

That phase arrived via CardStock's Catalog UAT (their D-116 brief, 2026-08-18): the
Browse wall showed 622 sets awaiting metadata, 395 of them Japanese. The owner then
widened the ask beyond the brief in two ways: the process must be **ongoing** — new
sets, Japanese or otherwise, must keep flowing in without anyone remembering to
refresh anything — and anything automatic must be **100% sure**, with everything
less certain left for manual handling rather than estimated into a column.

Probing TCGdex's `ja` locale (177 sets, pinned 2026-08-19) set the real ceiling:
15 of the 177 are corrupt upstream placeholders (the `CS*` block), it carries no
Japanese promo sets, and the DP and BW eras are absent entirely. Hand-curation
against the pinned mirror matched **161** sets — every legitimate row — each by
exact name translation with release-date and card-count coherence.

## Decision

1. **Japanese sets map through exactly one path: a hand-curated alias file**
   (`tcgdex-ja-set-aliases.json`, PriceCharting slug → TCGdex ja set id, a `reason`
   per row), resolved against a ja-locale catalog only. The partition wall stays
   for every other non-English shelf, and there is no name-matching fallback —
   `SetMapper` carries the ja inputs as an explicit `JapaneseShelf`, and a caller
   without one gets the old behavior: Japanese stays honestly `Unmapped`.
2. **One mirror directory per locale**, same directory-is-the-pin convention
   (`tcgdex-mirror-ja/` beside `tcgdex-mirror/`), and the manifest now records its
   locale so a directory can refuse a top-up as the wrong one.
3. **The pin learns to top up.** ADR-0009 marked delete-to-refresh *provisional*;
   this ADR evolves it: every `EnsureAsync` against an existing mirror re-reads the
   one-page set list and downloads only documents the directory lacks. New TCGdex
   sets arrive within a sweep; already-pinned documents never change; a failed
   top-up is a logged warning and the sweep proceeds on the existing pin (a TCGdex
   hiccup must not stall a lane — the 2026-08-17 lesson). Delete-to-repin remains
   the full refresh. The load-time count guard turned directional to make an
   interrupted top-up benign: surplus files load and heal; missing files still
   refuse. The first fetch got the same posture the hard way — its first
   production run died at document 103 of 177 on one stalled TLS read
   (2026-08-19, 11:14) and cost the sweep a day — so per-document downloads
   retry once on transport trouble, and an interrupted first fetch resumes past
   every document that already landed instead of starting over. One more
   top-up clause from the card audit: TCGdex publishes some sets before
   cataloguing their cards (92 of the 161 aliased ja sets on 2026-08-19), so a
   pinned document with an empty card list is re-checked every sweep until it
   stocks — their catalogue filling in confirms our cards automatically, with
   no manual re-pin.
4. **Japanese eras pool into the existing era codes** via new ordinal-exact
   Japanese serie-name keys in `tcgdex-series-eras.json` (剣と盾 → SWSH,
   サン＆ムーン → SM, PCG/ADV → EX, LEGEND → DP, neo/e/VS/web and the original
   series → WOTC, MEGA → ME), so CardStock's shelves merge with zero changes there.
5. **Per-card Japanese enrichment joins by collector number and is vouched for by
   species agreement, never by the name gate.** `CardNameAgreement` must never see
   Japanese text (the degenerate-equality hazard above). The replacement guard:
   the species tagged from the card's English PriceCharting title (`card_species`,
   ADR-0011) must be among the species named in the TCGdex ja card's Japanese name,
   resolved through the already-imported Japanese `species_names`. Cards where no
   species exists on either side (trainers, items, energy) get a new appended
   status — number matched, nothing written — because a wrong-set trainer collision
   would slip through an absence-agreement silently, and guessing is the one thing
   this join never does. This card phase ships only after its offline coverage
   audit is reviewed by the owner.

## Alternatives considered

- **EN↔JA fuzzy name matching** — rejected permanently, same grounds as ADR-0009:
  the alias file is the entire fuzziness budget, and it is reviewed by a human.
- **A merged two-locale catalog** — rejected: set ids can collide across locales,
  and every sibling/promo routing rule in the catalog is derived from folded
  English names that silently resolve to nothing against Japanese ones.
  Partition-scoped catalogs keep each shelf's rules honest.
- **A dexId fetch tier for the card guard** (per-card TCGdex documents,
  ~20k+ requests and a new mirror layer) — deferred as the escalation path if
  species agreement's audited coverage disappoints; the species names it needs are
  already imported and unread.
- **Scheduled re-pin by age** — rejected in favor of top-up: a monthly whole-mirror
  swap changes every pinned document at once and still leaves a new set invisible
  for weeks.

## Consequences

- 161 Japanese sets gain code, release date, series and era in one sweep;
  CardStock's pending wall drops 622 → 461 with zero CardStock changes. The
  remaining 234 are honest: TCGdex ja simply does not carry them (promos, DP/BW
  eras, Bandai/Carddass-class products, deck boxes).
- Curation is a standing loop, not a project: the sweep receipt logs a per-shelf
  matched/pending split, `ops/README.md` carries the worksheet query and the
  add-a-row runbook, and the alias file is deployed beside the blacklist — new
  rows are data changes, no code.
- The steady state costs one set-list request per locale per lane sweep.
- Three upstream TCGdex ja data bugs are documented in the alias file's `reason`
  fields rather than worked around in code: the `CS*` placeholder block (never
  aliased), and the corrupt display names on `SV4a` and `CP5` (aliased on
  date+count evidence; set_details never stores the ja display name).
- Manual metadata entry — for the sets and cards no join can reach — is
  deliberately **not addressed** here (owner, 2026-08-19); recorded as the
  expected next enrichment, not speculatively built.
