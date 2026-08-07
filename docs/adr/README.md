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
