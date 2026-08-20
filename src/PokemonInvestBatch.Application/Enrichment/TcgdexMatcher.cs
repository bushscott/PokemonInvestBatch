namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// The verdict of the join for one card — exactly what the enrichment table
/// stores, minus bookkeeping (when, against which mirror). Record equality
/// over these six fields IS the change-only test: a re-run whose verdict
/// equals the stored latest writes nothing.
/// </summary>
public sealed record EnrichmentVerdict
{
    public required TcgdexMatchStatus Status { get; init; }

    /// <summary>TCGdex's localId verbatim ("215", "TG23", "053") — the
    /// enrichment source's own spelling, replacing the render-time parse of
    /// PriceCharting's name. Confirmed only.</summary>
    public string? CardNumber { get; init; }

    /// <summary>The matched set's official printed size — the denominator in
    /// "215/203". Null when TCGdex publishes 0 for the set (new-era promo
    /// sets): a denominator of zero is a lie, not a size. Confirmed only.</summary>
    public int? SetOfficialSize { get; init; }

    public string? TcgdexSetId { get; init; }

    /// <summary>Deliberately N:1 — every PriceCharting variant product of one
    /// physical card ([1st Edition], [Shadowless], [Reverse Holo]) shares the
    /// TCGdex card. Never unique per product.</summary>
    public string? TcgdexCardId { get; init; }

    /// <summary>TCGdex's name for the candidate: the confirmation record on
    /// Confirmed, the review evidence on NameMismatch.</summary>
    public string? TcgdexName { get; init; }

    public static EnrichmentVerdict Of(TcgdexMatchStatus status) => new() { Status = status };
}

/// <summary>
/// Phase B of the join: one card's name against the routed TCGdex set(s).
/// Number-driven with the name as a confirmation gate — a number match is
/// never trusted until the names agree, and no verdict is ever forced.
/// </summary>
public static class TcgdexMatcher
{
    /// <summary>The Japanese card join's per-sweep inputs: the ja locale's
    /// own catalog (aliased entries' ids live there, never in the English
    /// one) and the species-agreement guard that replaces
    /// <see cref="CardNameAgreement"/> across scripts (ADR-0012). Per-card
    /// tagged species travel separately — they differ card to card.</summary>
    public sealed record JapaneseCardJoin(TcgdexCatalog Catalog, SpeciesAgreement Guard);

    public static EnrichmentVerdict Match(
        string cardName,
        SetMapEntry entry,
        TcgdexCatalog catalog,
        JapaneseCardJoin? japanese = null,
        IReadOnlySet<int>? taggedSpeciesIds = null)
    {
        var parts = CardNameParser.Parse(cardName);
        if (parts.Number is null)
        {
            return EnrichmentVerdict.Of(TcgdexMatchStatus.NoNumber);
        }

        if (entry.Kind == SetMapKind.Unmapped)
        {
            return EnrichmentVerdict.Of(TcgdexMatchStatus.UnmappedSet);
        }

        if (entry.Partition == SetPartition.Japanese)
        {
            // A mapped Japanese entry can only exist through the curated
            // alias table, and its join runs in its own lane: the ja
            // catalog, no sibling/promo prefix routing (those rules are
            // derived from folded English names and resolve to nothing
            // against Japanese ones), and the species guard instead of the
            // name gate — CardNameAgreement folds Japanese to nothing and
            // would confirm anything against anything.
            var ja = japanese ?? throw new InvalidOperationException(
                $"Set map entry '{entry.Slug}' is a mapped Japanese set, but the caller wired no Japanese " +
                "card join — refusing to guess.");
            return MatchJapanese(parts, entry, ja, taggedSpeciesIds ?? new HashSet<int>());
        }

        var prefix = CollectorNumber.AlphaPrefix(parts.Number);

        // The prefix names the sub-catalog the number lives in (TG23 numbers
        // exist in the Trainer Gallery sibling, SWSH262 in the SWSH promo
        // set), so routed sets are searched first and the primary set is only
        // a fallback. When both carry the number, the routed set's
        // denominator is the printed one.
        var routed = new List<TcgdexSet>();
        var primaries = new List<TcgdexSet>();
        if (entry.Kind == SetMapKind.PromoPool)
        {
            if (prefix.Length > 0)
            {
                if (catalog.PromoSetForPrefix(prefix) is { } eraPromos)
                {
                    routed.Add(eraPromos);
                }
            }
            else
            {
                // A bare-numbered promo could belong to any era; every promo
                // set is a candidate and the name gate plus the ambiguity
                // rule decide.
                routed.AddRange(catalog.PromoSets);
            }
        }
        else
        {
            foreach (var id in entry.TcgdexSetIds)
            {
                var primary = catalog.ById(id)
                    ?? throw new InvalidOperationException(
                        $"Set map for '{entry.Slug}' names TCGdex set '{id}', which the mirror does not contain.");
                primaries.Add(primary);
                switch (prefix)
                {
                    case "TG":
                        AddIfPresent(routed, catalog.TrainerGalleryOf(primary));
                        break;
                    case "GG":
                        AddIfPresent(routed, catalog.GalarianGalleryOf(primary));
                        break;
                    case "SV":
                        AddIfPresent(routed, catalog.ShinyVaultOf(primary));
                        break;
                    case "CC":
                        AddIfPresent(routed, catalog.ClassicCollectionOf(primary));
                        break;
                    case "RC":
                        routed.AddRange(catalog.RadiantCollections);
                        break;
                }

                // Era-prefixed promos filed inside a themed set (Celebrations
                // holds "Lance's Charizard V #SWSH133") route to the promo
                // set; the prefix cannot collide with a main set's numbering.
                AddIfPresent(routed, catalog.PromoSetForPrefix(prefix));
            }
        }

        return MatchIn(routed, parts) ?? MatchIn(primaries, parts)
            ?? EnrichmentVerdict.Of(TcgdexMatchStatus.NumberNotFound);
    }

    /// <summary>The Japanese verdict: number join within the aliased ja
    /// set(s) only, vouched for by species agreement. Every honest outcome
    /// the English path has, plus <see cref="TcgdexMatchStatus.NoSpeciesGuard"/>
    /// for the cards no cross-script guard can speak for.</summary>
    private static EnrichmentVerdict MatchJapanese(
        CardNameParts parts,
        SetMapEntry entry,
        JapaneseCardJoin japanese,
        IReadOnlySet<int> taggedSpeciesIds)
    {
        var canonical = CollectorNumber.Canonical(parts.Number!);
        var numberMatches = new List<(TcgdexSet Set, TcgdexCard Card)>();
        foreach (var id in entry.TcgdexSetIds)
        {
            var set = japanese.Catalog.ById(id)
                ?? throw new InvalidOperationException(
                    $"Set map for '{entry.Slug}' names TCGdex ja set '{id}', which the ja mirror does not contain.");
            foreach (var card in set.Cards)
            {
                if (CollectorNumber.Canonical(card.LocalId) == canonical)
                {
                    numberMatches.Add((set, card));
                }
            }
        }

        if (numberMatches.Count == 0)
        {
            return EnrichmentVerdict.Of(TcgdexMatchStatus.NumberNotFound);
        }

        if (taggedSpeciesIds.Count == 0)
        {
            return EnrichmentVerdict.Of(TcgdexMatchStatus.NoSpeciesGuard);
        }

        var agreeing = numberMatches
            .Where(m => japanese.Guard.SpeciesNamed(m.Card.Name).Overlaps(taggedSpeciesIds))
            .ToList();

        if (agreeing.Count == 1)
        {
            var (set, card) = agreeing[0];
            return new EnrichmentVerdict
            {
                Status = TcgdexMatchStatus.Confirmed,
                CardNumber = card.LocalId,
                SetOfficialSize = set.OfficialCount == 0 ? null : set.OfficialCount,
                TcgdexSetId = set.Id,
                TcgdexCardId = card.Id,
                TcgdexName = card.Name,
            };
        }

        if (agreeing.Count > 1)
        {
            return EnrichmentVerdict.Of(TcgdexMatchStatus.Ambiguous);
        }

        // No candidate agreed. If any candidate actually named a species,
        // the disagreement is real evidence (the wrong-set collision catch);
        // if none could, there was never a guard to satisfy.
        if (numberMatches.Any(m => japanese.Guard.SpeciesNamed(m.Card.Name).Count > 0))
        {
            var (evidenceSet, evidenceCard) = numberMatches[0];
            return new EnrichmentVerdict
            {
                Status = TcgdexMatchStatus.NameMismatch,
                TcgdexSetId = evidenceSet.Id,
                TcgdexCardId = evidenceCard.Id,
                TcgdexName = evidenceCard.Name,
            };
        }

        return EnrichmentVerdict.Of(TcgdexMatchStatus.NoSpeciesGuard);
    }

    /// <summary>Verdict from one candidate tier, or null when the number
    /// appears in none of its sets and the next tier should be tried.</summary>
    private static EnrichmentVerdict? MatchIn(IReadOnlyList<TcgdexSet> sets, CardNameParts parts)
    {
        var canonical = CollectorNumber.Canonical(parts.Number!);
        var numberMatches = new List<(TcgdexSet Set, TcgdexCard Card)>();
        foreach (var set in sets)
        {
            foreach (var card in set.Cards)
            {
                if (CollectorNumber.Canonical(card.LocalId) == canonical)
                {
                    numberMatches.Add((set, card));
                }
            }
        }

        if (numberMatches.Count == 0)
        {
            return null;
        }

        var agreeing = numberMatches
            .Where(m => CardNameAgreement.Agree(parts.BaseName, m.Card.Name))
            .ToList();

        if (agreeing.Count == 1)
        {
            var (set, card) = agreeing[0];
            return new EnrichmentVerdict
            {
                Status = TcgdexMatchStatus.Confirmed,
                CardNumber = card.LocalId,
                SetOfficialSize = set.OfficialCount == 0 ? null : set.OfficialCount,
                TcgdexSetId = set.Id,
                TcgdexCardId = card.Id,
                TcgdexName = card.Name,
            };
        }

        if (agreeing.Count > 1)
        {
            // Same number, same name, more than one candidate — bare-numbered
            // promos collide across eras this way. Guessing is the one thing
            // this join never does.
            return EnrichmentVerdict.Of(TcgdexMatchStatus.Ambiguous);
        }

        // The number exists but no candidate's name agrees. Record the first
        // candidate as review evidence — this is Celebrations Classic
        // Collection's honest landing ("Charizard #4" meeting Celebrations
        // #4, Palkia).
        var (evidenceSet, evidenceCard) = numberMatches[0];
        return new EnrichmentVerdict
        {
            Status = TcgdexMatchStatus.NameMismatch,
            TcgdexSetId = evidenceSet.Id,
            TcgdexCardId = evidenceCard.Id,
            TcgdexName = evidenceCard.Name,
        };
    }

    private static void AddIfPresent(List<TcgdexSet> sets, TcgdexSet? set)
    {
        if (set is not null && !sets.Contains(set))
        {
            sets.Add(set);
        }
    }
}
