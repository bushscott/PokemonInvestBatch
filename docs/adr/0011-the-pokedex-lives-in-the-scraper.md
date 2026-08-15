# ADR-0011: The Pokédex lives in the scraper

**Date:** 2026-08-14
**Status:** Accepted

## Context

CardStock's Card page subline is designed to read `{set} · 215/203 · {character}`.
ADR-0009 built the first two fields from a pinned TCGdex mirror and explicitly
declined the third: the owner scoped species out of that enrichment and out of
CardStock's Phase 2 entirely, deferring it to "another phase" that would "have
to create a Pokedex, and it will belong in there" (CardStock `DECISIONS.md`
D-084, item 10, 2026-08-12). CardStock shipped Phase 2 on that scope — the
Card page's character segment renders a placeholder today ("Pokémon name —
arrives with the Pokédex phase," D-087) — and its Character page design
already commits to a species icon in that slot (D-104), with Browse expected
to want the same field for its species tiles.

On 2026-08-14 the owner reopened where that Pokédex should live and reversed
the earlier scoping: *"I consider this scraping and would like it to be in the
scraping app"* (CardStock D-106). The reasoning: matching a card's title
against a species catalog is the same kind of work this repo already does
everywhere else it derives state from `cards.name` — title parsing over a
corpus this repo owns — not a computation CardStock should run over data it
does not hold. The same exchange settled the sourcing and shape questions
this ADR records: a pinned PokéAPI dataset for species facts, the PokeAPI
sprites repository for icons, plain title matching for tagging, and read-only
consumption for CardStock — no new grant, no cross-schema write.

This ADR records that shape before Task 2 of the implementation plan writes a
single migration.

**Licensing, checked the same way as ADR-0009's TCGdex mirror.**
`PokeAPI/api-data` is BSD-3-Clause (verified live 2026-08-14, recorded in
CardStock D-106): redistribution, modification and commercial use permitted,
the only obligation is preserving the copyright notice — the same shape as
TCGdex's MIT. `PokeAPI/sprites` is CC0 1.0 Universal, and unlike TCGdex's MIT
its own `LICENCE.txt` states the compilation/IP split outright rather than
leaving it to inference: *"All image contents within are Copyright The
Pokémon Company."* Both grants cover PokeAPI's compilation — the dataset's
assembly, the sprite files' organization — not the Pokémon IP the data and
sprites depict; the icons' IP posture rides the same non-affiliation stance
this project already carries (ADR-0009).

## Decision

**1. Seven new scraper-owned tables.**

| Table | Holds |
|---|---|
| `species` | One row per national dex number (~1,025) — name, slug, generation, region, color, habitat, legendary/mythical status, evolution stage and parent, gradient hex pair |
| `species_types` | 1–2 rows per species — its PokéAPI type(s) |
| `species_egg_groups` | 1–2 rows per species — its egg group(s), display-named |
| `species_names` | One row per species per dataset language (12, including Japanese) — imported now because the dataset carries it free; unused by any reader until a later phase |
| `card_species` | The card ↔ species junction — a card can name more than one species ("Pikachu & Zekrom GX") |
| `card_tagging` | One row per taggable card, always — the tagging verdict (`Tagged`/`NoSpecies`/`Quarantined`), the method, and the exact title text matched |
| `set_details` | One row per set, always — era/series, release date, and set code, joined from the existing TCGdex mapping (ADR-0009) where set names match; `Pending` elsewhere. Series→era resolution reads a curated mapping file in the same posture as `tcgdex-set-aliases.json`; the option that names it (`TcgdexSeriesEraPath`) is reserved by this ADR but unused until that sweep is built later in this phase |

This mirrors `tcgdex_enrichments`' precedent: a status column where "not yet
matched" is a first-class value, never absence. Column types and constraints
are Task 2's migration, not this ADR's job — the shape above is the
commitment this ADR makes.

**2. A pinned local mirror for both the PokéAPI dataset and its sprites,
fetched once — never a live dependency.** Same pattern as ADR-0009's TCGdex
mirror: `PokeapiDataBaseUrl`/`PokeapiSpritesBaseUrl` point at
`raw.githubusercontent.com`, the pin is a commit SHA that is itself the next
path segment, and refreshing means bumping the pin default and deleting the
mirror directory so the next sweep re-fetches from the new commit.
`PokedexMirrorDirectory` (dataset) and `SpeciesIconDirectory` (sprites) are
separate directories, because the two repos pin independently and a
data-only or sprite-only refresh should not force a re-fetch of the other.

**3. Tagging is longest-species-name-first title matching, word-boundary
safe.** A card's normalized title is scanned against every species name,
longest first, so "Porygon2" and "Porygon-Z" claim their own text before
plain "Porygon" can consume it as a prefix; boundaries are enforced so
"Charizard" cannot match inside a longer unrelated word, and a consumed span
is removed from the buffer so one occurrence cannot double-match two
species. An alias table absorbs gender glyphs and hyphen variants; a
denylist stops Pokémon-named non-Pokémon products ("Charizard Spirit Link,"
"Clefairy Doll") from tagging as their namesake; four or more candidate hits
on one title quarantine instead of guessing. English-only matching is
sufficient for the whole corpus — only 51 of 91,646 active cards carry any
non-ASCII character in `cards.name`, and every one is punctuation, not a
different language (CardStock D-105).

**4. The named-species rule: a card tags only when its title names a
species.** Artwork depicting a Pokémon without naming one — a cameo, a
background appearance — is untaggable by this design, on purpose. There is
no text to match against, and inferring a species from image content is a
different, more expensive problem this ADR does not take on.

**5. Deviation one: `card_species` and `card_tagging` are current-state
tables, not append-only.** Every history table in this schema
(`price_months`, `populations`, `sales`, `tcgdex_enrichments`) is append-only
and change-only, because each row is an observation of the world at a point
in time, and an observation is never wrong to keep. A tagging verdict is not
an observation — it is the current output of a matching process run against
the current `cards.name`, and a stale verdict (a title correction, an
alias-table fix, a re-pinned dataset) is not history worth keeping beside the
new one; it is simply wrong. Layering a newer row beside a stale one would
make every reader responsible for picking `MAX(computed_at)`, the same
discipline `tcgdex_enrichments` already demands — a cost worth paying once,
not worth adding again for no reason. The species-dataset tables (`species`
and its three children) get the same treatment for the same reason: the
source is a re-fetchable pinned snapshot, not an event, so importing it is an
upsert, not an append.

**6. Deviation two: `pokemon_app` receives targeted `UPDATE`/`DELETE` on
exactly these tables.** `postgres-setup.sql` states the rule this role has
held since its first migration: "No DELETE anywhere — the store is
append-only by design, and the role enforces it," and today that holds
without exception — the sales-gap cut runs as the owner role specifically
because `pokemon_app` cannot (`ops/README.md:139`). This ADR is the first
crack in that rule, and it is deliberately narrow: `UPDATE` on all seven new
tables (the importer and the tagging lane overwrite rows in place); `DELETE`
additionally on `card_species` (a title correction must remove the old
species link, not leave it beside the new one — a stale link here is a wrong
answer on the Character page) and on the three species child tables
`species_types`/`species_egg_groups`/`species_names` (a changed species'
children are replaced wholesale on re-import rather than diffed row by row).
No other table's grants change. The exact `GRANT` statements are ops
documentation — the last task of this phase's implementation plan — and this
ADR is the rationale they cite.

**7. Manual overrides are operator SQL, the same posture as delisting.**
ADR-0002 settled this shape for `delisted_at`: a human runs a documented
statement, the application honours the result but never writes it itself.
`card_species`/`card_tagging` rows written with `method = Manual` follow the
identical rule — an operator statement, never a console verb or an API
route — and the tagging lane's re-tagging pass leaves every `Manual` row
untouched on every run, until another human statement changes it.

**8. The pins.** `PokeapiDataPin` = `2cda0b56a3a8ad2529d8aac73528225f96d2c848`
(`PokeAPI/api-data`, default-branch HEAD at the time of this ADR).
`PokeapiSpritesPin` = `c10459b9b0129eaca5c5d9b1cac65336debb1d08`
(`PokeAPI/sprites`, same). Both read via `git ls-remote <repo> HEAD` on
2026-08-14 and recorded here as the `ScraperOptions` defaults in the same
commit as this ADR.

## Alternatives considered

**Species tagging inside CardStock, against the scraper's existing tables
(the plan until 2026-08-14).** Rejected once the owner named what the work
actually is: title parsing over a corpus CardStock does not own and has no
scraping infrastructure for. Keeping it here means one process, one
vocabulary ("derive current state from `cards.name`"), and no new cross-repo
dependency for CardStock to stand up.

**TCGdex as the species source, reusing ADR-0009's mirror.** TCGdex's
card-level responses carry `dexId`, so the join is technically available.
Rejected: the species-level facts CardStock's Character page wants — color,
habitat, egg groups, generation, evolution chains, localized names — are not
part of what TCGdex's card endpoint returns, and joining species through
TCGdex's per-card matches would inherit that enrichment's roughly 53%
non-English/unmapped-set coverage gap (ADR-0009) for a field that has
nothing to do with which set a card belongs to. PokéAPI's dataset is
complete for the whole national dex regardless of which cards PriceCharting
carries.

**pokemondb.net as the icon/data source.** Rejected per D-106: its own About
page asks not to be scraped ("Do not steal our content!" and recommends
PokeAPI instead), and its data lacks color and habitat regardless.

**Self-hosting a PokeAPI instance (Docker) instead of a static mirror.**
Works, and PokeAPI publishes one, but stands up a service on the Pi to
answer ~1,025 species' worth of JSON this code can read from disk just as
well. The same call ADR-0009 made about self-hosting TCGdex; reconsider only
if a future phase needs a live query neither mirror can answer.

**Live PokéAPI calls, no mirror.** Couples every sweep to PokéAPI's uptime
and lets the dataset shift under re-runs, for data that is small, static
between releases, and fits on disk. Rejected for the same reason ADR-0009
rejected live TCGdex calls.

## Consequences

**Good:**
- CardStock's Character and Browse surfaces get a real species link, an icon
  corpus, and set metadata, all through the read access it already has —
  this ADR changes no grant on CardStock's side and adds no schema straddling
  two repos.
- The tagging and import tables are derived and rebuildable by construction:
  a bad alias, a bad title match, or a dataset re-pin all heal with a clean
  sweep re-run, never a hand repair or a migration.
- Manual corrections survive every re-run in the same durable,
  human-statement shape ADR-0002 already established for delisting — one
  pattern for "a human overrode the machine" across the whole repo.
- The mirror means this Pokédex has no runtime dependency on PokeAPI's
  availability after the first fetch, matching ADR-0009's mirror precedent
  exactly.

**Costs:**
- `pokemon_app` carries `DELETE` for the first time in this schema's
  history, on four tables. A bug in the tagging lane's diff logic can now
  remove real rows in a way no earlier lane could; the blast radius is
  bounded to `card_species` and the three species child tables, and nothing
  outside this ADR's seven tables is affected.
- Art-cameo cards are permanently untaggable by this design — stated
  honestly rather than left as a silent gap. Catching them would need image
  recognition or manual curation at a scale this ADR does not attempt.
- The pin fixes both mirrors to one commit each. A PokéAPI correction after
  that commit (a fixed habitat, a newly split form) waits for an operator
  re-pin, the same tradeoff ADR-0009 accepts for TCGdex.
- `species_names` imports 12 languages of data that nothing reads yet — a
  deliberate bet that it is cheaper to take now, while the dataset is
  already being fetched, than to re-fetch and re-migrate later.
