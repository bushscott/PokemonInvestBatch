# Architecture Decision Records

Each file records one significant decision: what the situation was, what was chosen, what was
rejected and why, and what it costs. They are numbered in the order the decisions were made and
are not edited afterwards — if a decision is reversed, a new ADR supersedes the old one, so the
reasoning trail stays intact.

The format is Michael Nygard's, from *Documenting Architecture Decisions* (2011).

| ADR | Decision | Date |
|---|---|---|
| [0001](0001-append-only-history.md) | History is append-only and change-only | 2026-07-27 |
| [0002](0002-manual-only-delisting.md) | Retiring a dead card is a human decision, never automatic | 2026-08-03 |
| [0003](0003-functional-core-over-ports-and-adapters.md) | Pure decision classes instead of interfaces everywhere | 2026-07-27 |
| [0004](0004-card-faults-do-not-slow-the-crawl.md) | A broken page must not slow down the whole crawl | 2026-08-06 |
| [0005](0005-pooled-grade-tiers.md) | Grading companies are pooled below grade 10 | 2026-08-04 |
| [0006](0006-localhost-intake-api-and-express-visits.md) | A localhost intake API, with express visits outside the polite gate | 2026-08-09 |
| [0007](0007-schedule-on-the-hottest-buckets-pace.md) | The schedule follows the hottest bucket, and a capped card warns its set | 2026-08-10 |
| [0008](0008-express-visits-have-no-time-barriers.md) | Express visits have no time barriers; the calling app owns the rate limit | 2026-08-10 |
| [0009](0009-tcgdex-metadata-enrichment.md) | Collector numbers and set sizes join from a pinned TCGdex mirror | 2026-08-13 |
| [0010](0010-machine-retirement-with-brakes.md) | The listing retires a card, the probe brings it back (amends 0002) | 2026-08-13 |
| [0011](0011-the-pokedex-lives-in-the-scraper.md) | The Pokédex — species, card tagging, and set metadata — is scraper-owned | 2026-08-14 |
| [0012](0012-the-japanese-shelf-joins-by-hand.md) | The Japanese shelf joins by hand-curated alias; the mirror learns to top up | 2026-08-19 |
