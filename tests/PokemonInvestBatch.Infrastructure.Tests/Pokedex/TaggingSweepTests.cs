using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Infrastructure.Pokedex;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Infrastructure.Tests.Pokedex;

/// <summary>
/// <see cref="TaggingSweep.RunAsync"/> against real PostgreSQL. Each test
/// builds and drops its own database; see DatabaseTest.
///
/// The candidate list below is Task 5's trap fixture
/// (SpeciesMatcherTests.Candidates) copied verbatim, so every id used here
/// traces back to a matcher case already proven correct — this suite's job
/// is the sweep wrapped around <see cref="SpeciesMatcher.Match"/> (work-set
/// selection, the card_tagging upsert, the card_species diff), not the
/// matching rules themselves.
/// </summary>
public class TaggingSweepTests : DatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<(string Name, int SpeciesId)> Candidates =
        SpeciesMatcher.BuildCandidates(new (int Id, string EnglishName)[]
        {
            (25, "Pikachu"),
            (26, "Raichu"),
            (172, "Pichu"),
            (150, "Mewtwo"),
            (151, "Mew"),
            (140, "Kabuto"),
            (141, "Kabutops"),
            (137, "Porygon"),
            (233, "Porygon2"),
            (474, "Porygon-Z"),
            (29, "Nidoran♀"),
            (32, "Nidoran♂"),
            (30, "Nidorina"),
            (33, "Nidorino"),
            (83, "Farfetch'd"),
            (122, "Mr. Mime"),
            (439, "Mime Jr."),
            (772, "Type: Null"),
            (37, "Vulpix"),
            (197, "Umbreon"),
            (644, "Zekrom"),
            (120, "Staryu"),
            (6, "Charizard"),
            (35, "Clefairy"),
            (669, "Flabébé"),
            (1002, "Chien-Pao"),
        });

    [SkippableFact]
    public async Task A_fresh_card_tags_and_links_its_species()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(197, "Umbreon", "umbreon"));
            seed.Cards.Add(SeedCard(1, "Umbreon VMAX #215"));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Tagged);
        Assert.Equal(0, result.NoSpecies);
        Assert.Equal(0, result.Quarantined);
        Assert.Equal(1, result.LinksWritten);
        Assert.Equal(0, result.LinksRemoved);

        await using var verify = NewContext();
        var tagging = await verify.CardTagging.SingleAsync(t => t.CardId == 1);
        Assert.Equal(TagStatus.Tagged, tagging.Status);
        Assert.Equal(TagMethod.TitleMatch, tagging.Method);
        Assert.Equal("Umbreon VMAX #215", tagging.TaggedName);
        Assert.Equal(Now, tagging.UpdatedAt);

        var link = await verify.CardSpecies.SingleAsync(l => l.CardId == 1);
        Assert.Equal(197, link.SpeciesId);
        Assert.Equal(TagMethod.TitleMatch, link.Method);
    }

    [SkippableFact]
    public async Task A_trainer_card_gets_no_species_and_no_links()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.Cards.Add(SeedCard(2, "Rare Candy #85"));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Tagged);
        Assert.Equal(1, result.NoSpecies);
        Assert.Equal(0, result.LinksWritten);

        await using var verify = NewContext();
        var tagging = await verify.CardTagging.SingleAsync(t => t.CardId == 2);
        Assert.Equal(TagStatus.NoSpecies, tagging.Status);
        Assert.Equal(TagMethod.TitleMatch, tagging.Method);
        Assert.Equal(0, await verify.CardSpecies.CountAsync());
    }

    [SkippableFact]
    public async Task A_quarantined_verdict_writes_the_row_but_no_links()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            // Same 5-species collision as SpeciesMatcherTests'
            // Quarantines_FourOrMoreSpeciesInOneTitle fixture (R5).
            seed.Cards.Add(SeedCard(3, "Pikachu & Raichu & Mewtwo & Zekrom & Staryu #1"));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Tagged);
        Assert.Equal(0, result.NoSpecies);
        Assert.Equal(1, result.Quarantined);
        Assert.Equal(0, result.LinksWritten);

        await using var verify = NewContext();
        var tagging = await verify.CardTagging.SingleAsync(t => t.CardId == 3);
        Assert.Equal(TagStatus.Quarantined, tagging.Status);
        Assert.Equal(TagMethod.TitleMatch, tagging.Method);
        Assert.Equal(0, await verify.CardSpecies.CountAsync(l => l.CardId == 3));
    }

    [SkippableFact]
    public async Task A_manual_link_on_a_different_species_survives_a_machine_run()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(197, "Umbreon", "umbreon"));
            seed.SpeciesRows.Add(SeedSpecies(25, "Pikachu", "pikachu"));
            seed.Cards.Add(SeedCard(4, "Umbreon VMAX #215"));
            // An operator's manual addition of a second character this
            // title never actually names — contrived, but it proves the
            // diff never removes a Manual row regardless of what the
            // machine verdict wants.
            seed.CardSpecies.Add(new CardSpeciesLink { CardId = 4, SpeciesId = 25, Method = TagMethod.Manual });
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(1, result.LinksWritten); // only the new Umbreon link
        Assert.Equal(0, result.LinksRemoved);

        await using var verify = NewContext();
        var links = await verify.CardSpecies.Where(l => l.CardId == 4).OrderBy(l => l.SpeciesId).ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(25, links[0].SpeciesId);
        Assert.Equal(TagMethod.Manual, links[0].Method);
        Assert.Equal(197, links[1].SpeciesId);
        Assert.Equal(TagMethod.TitleMatch, links[1].Method);
    }

    [SkippableFact]
    public async Task A_manual_link_on_the_same_species_the_title_matches_is_never_duplicated()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(197, "Umbreon", "umbreon"));
            seed.Cards.Add(SeedCard(5, "Umbreon VMAX #215"));
            // An operator already linked the one species this title would
            // also machine-match. (card_id, species_id) is the whole
            // primary key on card_species, so an insert here would collide
            // if the sweep ever tried to add its own TitleMatch row for the
            // same pair.
            seed.CardSpecies.Add(new CardSpeciesLink { CardId = 5, SpeciesId = 197, Method = TagMethod.Manual });
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(0, result.LinksWritten);
        Assert.Equal(0, result.LinksRemoved);

        await using var verify = NewContext();
        var link = await verify.CardSpecies.SingleAsync(l => l.CardId == 5);
        Assert.Equal(TagMethod.Manual, link.Method); // still Manual, never overwritten
    }

    [SkippableFact]
    public async Task A_rename_swaps_the_link_and_updates_tagged_name()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(150, "Mewtwo", "mewtwo"));
            seed.SpeciesRows.Add(SeedSpecies(151, "Mew", "mew"));
            seed.Cards.Add(SeedCard(6, "Mewtwo #10"));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);
        var sweep = new TaggingSweep();

        await using (var first = NewContext())
        {
            var firstResult = await sweep.RunAsync(first, Candidates, clock, CancellationToken.None);
            Assert.Equal(1, firstResult.Tagged);
        }

        await using (var rename = NewContext())
        {
            (await rename.Cards.SingleAsync(c => c.Id == 6)).Name = "Mew #8";
            await rename.SaveChangesAsync();
        }

        clock.SetUtcNow(Now.AddHours(1));
        await using (var second = NewContext())
        {
            var result = await sweep.RunAsync(second, Candidates, clock, CancellationToken.None);

            Assert.Equal(1, result.Examined);
            Assert.Equal(1, result.Tagged);
            Assert.Equal(1, result.LinksWritten);
            Assert.Equal(1, result.LinksRemoved);
        }

        await using var verify = NewContext();
        var tagging = await verify.CardTagging.SingleAsync(t => t.CardId == 6);
        Assert.Equal("Mew #8", tagging.TaggedName);
        Assert.Equal(Now.AddHours(1), tagging.UpdatedAt);

        // The old (card, 150) link is gone; (card, 151) is the only one left.
        var link = await verify.CardSpecies.SingleAsync(l => l.CardId == 6);
        Assert.Equal(151, link.SpeciesId);
    }

    [SkippableFact]
    public async Task A_delisted_card_is_still_examined_and_tagged()
    {
        // Acceptance invariant #1 (ops/README.md §8): every taggable card
        // gets exactly one card_tagging row — delisted or not. Only
        // not_a_card_at excludes (see A_not_a_card_page_is_never_examined,
        // below); delisted_at must never join that filter.
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(197, "Umbreon", "umbreon"));
            var card = SeedCard(10, "Umbreon VMAX #215");
            card.DelistedAt = Now;
            seed.Cards.Add(card);
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Tagged);
    }

    [SkippableFact]
    public async Task A_not_a_card_page_is_never_examined()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            // A title that WOULD tag as Pikachu, if it were ever looked at.
            seed.Cards.Add(SeedCard(7, "Pikachu Plush", notACardAt: Now));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(0, result.Examined);

        await using var verify = NewContext();
        Assert.Equal(0, await verify.CardTagging.CountAsync());
    }

    [SkippableFact]
    public async Task A_manually_pinned_card_is_skipped_even_after_its_name_changes()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.Cards.Add(SeedCard(8, "New Name #1"));
            // The operator pinned this card under its old name — R10: the
            // pin freezes the card, changed name or not, until a human
            // statement (never a sweep) unpins it.
            seed.CardTagging.Add(new CardTagging
            {
                CardId = 8,
                Status = TagStatus.NoSpecies,
                Method = TagMethod.Manual,
                TaggedName = "Old Name #1",
                UpdatedAt = Now.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);

        await using var db = NewContext();
        var result = await new TaggingSweep().RunAsync(db, Candidates, clock, CancellationToken.None);

        Assert.Equal(0, result.Examined);

        await using var verify = NewContext();
        var tagging = await verify.CardTagging.SingleAsync(t => t.CardId == 8);
        Assert.Equal(TagMethod.Manual, tagging.Method);
        Assert.Equal("Old Name #1", tagging.TaggedName); // untouched
        Assert.Equal(Now.AddDays(-1), tagging.UpdatedAt); // untouched
    }

    [SkippableFact]
    public async Task A_second_run_over_unchanged_data_examines_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet());
            seed.SpeciesRows.Add(SeedSpecies(197, "Umbreon", "umbreon"));
            seed.Cards.Add(SeedCard(9, "Umbreon VMAX #215"));
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(Now);
        var sweep = new TaggingSweep();

        await using (var first = NewContext())
        {
            await sweep.RunAsync(first, Candidates, clock, CancellationToken.None);
        }

        await using var second = NewContext();
        var result = await sweep.RunAsync(second, Candidates, clock, CancellationToken.None);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Tagged);
        Assert.Equal(0, result.NoSpecies);
        Assert.Equal(0, result.Quarantined);
        Assert.Equal(0, result.LinksWritten);
        Assert.Equal(0, result.LinksRemoved);
    }

    private static CardSet SeedSet() => new()
    {
        Id = 1,
        Slug = "pokemon-evolving-skies",
        Name = "Pokemon Evolving Skies",
        DiscoveredAt = Now,
        LastSeenAt = Now,
    };

    private static Card SeedCard(long id, string name, DateTimeOffset? notACardAt = null) => new()
    {
        Id = id,
        SetId = 1,
        Url = $"/game/pokemon-evolving-skies/card-{id}",
        Name = name,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        NotACardAt = notACardAt,
    };

    private static Species SeedSpecies(int id, string name, string slug) => new()
    {
        Id = id,
        Name = name,
        Slug = slug,
        Generation = 1,
        Region = "Kanto",
        Color = "Red",
        Status = SpeciesStatus.Ordinary,
        Stage = 0,
        GradientStart = "#000000",
        GradientEnd = "#111111",
    };
}
