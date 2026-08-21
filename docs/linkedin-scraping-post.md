# LinkedIn post — scraping headaches & solutions (week of 2026-08-17)

## Final draft

I track live sales for 34,000 Pokémon cards. Last week I quietly slowed the whole thing down — and didn't notice for a day.

I'm building a Pokémon card investment product, and this week's chapter is the data engine that watches the market.

My data source is a conveyor belt with no rewind button. The pricing site I watch shows only the newest ~30 sales per grade, per card. If a card sells faster than I come back, older sales scroll off forever. Scraping here isn't downloading — it's racing.

And I can't just sprint: I cap the crawler at ~8,300 polite page visits a day across tens of thousands of cards. So every card runs on its own revisit clock, set by how fast it actually sells. The fastest-selling grade sets the pace, because it loses data first. A hot card gets seen every ~7 hours; a sleepy one, monthly.

Then I wrote a backfill that recomputed every card's sales rate anchored on the day the script ran — not the day the data was captured. Every card suddenly looked ~1.8x slower than it really was. The clocks stretched. A hot Pikachu lost real sales overnight before I caught it.

The tests were green the whole time. They encoded the same wrong assumption the code did. A green test proves your code agrees with you — not that you're right.

So I stopped trusting myself and built proof instead.

Proof by overlap: if a freshly fetched page still shares even one sale with what I've already stored, the chain is unbroken — nothing slipped between visits. Zero overlap means sales rolled off unseen. No timestamps, no guessing at page sizes.

And a near-miss smoke detector: when a page comes back nearly all-new, the next visit interval halves automatically. The alarm fires before data is lost, not after.

All of it runs on a Raspberry Pi on a shelf at home, behind ~870 automated tests — which I now treat as opinions. The overlap proof is the verdict.

What's the quietest bug you've ever shipped — the one no alarm caught?

#buildinpublic #dataengineering #pokemon

## Before you post — specifics to approve or strike

- **~8,300 visits/day** — quantifies the scraping scale publicly. Reads as "polite/self-limited", but it's the one operational number in the post. Easy to soften to "a strict daily visit budget".
- **The 34k / 1.8x incident + "a hot Pikachu lost real sales"** — public admission of a shipped bug. It's the heart of the post; strike only if you'd rather not own it publicly.
- **Raspberry Pi on a shelf at home** — charming and very #buildinpublic, but it does say where prod lives.
- The data source is deliberately never named.

## Banked for a future post

The self-healing disappearance machinery: cards get renamed or delisted upstream; repeated redirects trigger a walk of the set listing — renames heal same-day, removals retire quietly, retired cards get probed on a doubling clock in case they come back (some already have). A complete story on its own: "my scraper notices when products stop existing."

Also unused, from the runner-up drafts: the liquidation detective story ("the alert said 30 sales/day; it was one seller and a binder — sequential listing ids, 11 copies at exactly $40") and the line "Not every belt problem is a speed problem."
