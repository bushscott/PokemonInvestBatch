using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Infrastructure.Tests.Persistence;

/// <summary>
/// The seven Pokédex tables (ADR-0011), against real PostgreSQL. Each test
/// builds and drops its own database; see DatabaseTest.
/// </summary>
public class PokedexPersistenceTests : DatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task A_species_and_its_tagged_card_round_trip_every_field()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var seed = NewContext())
        {
            await SeedSpeciesAndCardAsync(seed);
            seed.CardSpecies.Add(new CardSpeciesLink { CardId = 630417, SpeciesId = 6, Method = TagMethod.TitleMatch });
            seed.CardTagging.Add(new CardTagging
            {
                CardId = 630417,
                Status = TagStatus.Tagged,
                Method = TagMethod.TitleMatch,
                TaggedName = "Charizard #4",
                UpdatedAt = Now,
            });
            seed.SetDetails.Add(new SetDetail
            {
                SetId = 1,
                MatchStatus = SetMatchStatus.Matched,
                Code = "base1",
                ReleasedOn = new DateOnly(1999, 1, 9),
                Series = "Base",
                Era = "Base Era",
            });
            await seed.SaveChangesAsync();
        }

        // A fresh context, so this reads what Postgres actually stored rather
        // than echoing the first context's change tracker.
        await using var db = NewContext();

        var species = await db.SpeciesRows.SingleAsync(s => s.Id == 6);
        Assert.Equal("Charizard", species.Name);
        Assert.Equal("charizard", species.Slug);
        Assert.Equal((short)1, species.Generation);
        Assert.Equal("Kanto", species.Region);
        Assert.Equal("Red", species.Color);
        Assert.Equal("mountain", species.Habitat);
        Assert.Equal(SpeciesStatus.Ordinary, species.Status);
        Assert.Equal((short)2, species.Stage);
        Assert.Equal(5, species.EvolvesFromSpeciesId);
        Assert.Equal("#F08030", species.GradientStart);
        Assert.Equal("#F5AC78", species.GradientEnd);

        var types = await db.SpeciesTypes.Where(t => t.SpeciesId == 6).OrderBy(t => t.Slot).ToListAsync();
        Assert.Equal(2, types.Count);
        Assert.Equal("Fire", types[0].Type);
        Assert.Equal((short)1, types[0].Slot);
        Assert.Equal("Flying", types[1].Type);
        Assert.Equal((short)2, types[1].Slot);

        var eggGroups = await db.SpeciesEggGroups
            .Where(e => e.SpeciesId == 6)
            .Select(e => e.EggGroup)
            .OrderBy(name => name)
            .ToArrayAsync();
        Assert.Equal(["Dragon", "Monster"], eggGroups);

        var names = await db.SpeciesNames.Where(n => n.SpeciesId == 6).OrderBy(n => n.Language).ToListAsync();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, n => n.Language == "en" && n.Name == "Charizard");
        Assert.Contains(names, n => n.Language == "ja" && n.Name == "リザードン");

        var link = await db.CardSpecies.SingleAsync(l => l.CardId == 630417 && l.SpeciesId == 6);
        Assert.Equal(TagMethod.TitleMatch, link.Method);

        var tagging = await db.CardTagging.SingleAsync(t => t.CardId == 630417);
        Assert.Equal(TagStatus.Tagged, tagging.Status);
        Assert.Equal(TagMethod.TitleMatch, tagging.Method);
        Assert.Equal("Charizard #4", tagging.TaggedName);
        Assert.Equal(Now, tagging.UpdatedAt);

        var detail = await db.SetDetails.SingleAsync(d => d.SetId == 1);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("base1", detail.Code);
        Assert.Equal(new DateOnly(1999, 1, 9), detail.ReleasedOn);
        Assert.Equal("Base", detail.Series);
        Assert.Equal("Base Era", detail.Era);
    }

    [SkippableFact]
    public async Task A_duplicate_card_species_link_is_rejected()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var seed = NewContext())
        {
            await SeedSpeciesAndCardAsync(seed);
            seed.CardSpecies.Add(new CardSpeciesLink { CardId = 630417, SpeciesId = 6, Method = TagMethod.TitleMatch });
            await seed.SaveChangesAsync();
        }

        // A fresh context: reusing the seeding context would have EF's own
        // change tracker reject the second Add before the database ever gets
        // a say, which would prove the wrong thing.
        await using var db = NewContext();
        db.CardSpecies.Add(new CardSpeciesLink { CardId = 630417, SpeciesId = 6, Method = TagMethod.Manual });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_species_claiming_to_evolve_from_a_nonexistent_species_is_rejected()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using var db = NewContext();
        db.SpeciesRows.Add(new Species
        {
            Id = 1,
            Name = "Bulbasaur",
            Slug = "bulbasaur",
            Generation = 1,
            Region = "Kanto",
            Color = "Green",
            Habitat = "grassland",
            Status = SpeciesStatus.Ordinary,
            Stage = 0,
            // No species 999 exists — proves the self-FK is enforced, not
            // just declared. A real Stage 0 species would leave this null.
            EvolvesFromSpeciesId = 999,
            GradientStart = "#78C850",
            GradientEnd = "#A7DB8D",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task SeedSpeciesAndCardAsync(PokemonDbContext db)
    {
        // Charizard's EvolvesFromSpeciesId points here — a real row, not a
        // bare int, now that the self-FK is enforced. Its own
        // EvolvesFromSpeciesId is left null rather than chased back to
        // Charmander; one extra link is enough to prove a real reference
        // round-trips without modelling the whole chain.
        db.SpeciesRows.Add(new Species
        {
            Id = 5,
            Name = "Charmeleon",
            Slug = "charmeleon",
            Generation = 1,
            Region = "Kanto",
            Color = "Red",
            Habitat = "mountain",
            Status = SpeciesStatus.Ordinary,
            Stage = 1,
            EvolvesFromSpeciesId = null,
            GradientStart = "#F08030",
            GradientEnd = "#F5AC78",
        });
        db.SpeciesRows.Add(new Species
        {
            Id = 6,
            Name = "Charizard",
            Slug = "charizard",
            Generation = 1,
            Region = "Kanto",
            Color = "Red",
            Habitat = "mountain",
            Status = SpeciesStatus.Ordinary,
            Stage = 2,
            EvolvesFromSpeciesId = 5,
            GradientStart = "#F08030",
            GradientEnd = "#F5AC78",
        });
        db.SpeciesTypes.AddRange(
            new SpeciesType { SpeciesId = 6, Slot = 1, Type = "Fire" },
            new SpeciesType { SpeciesId = 6, Slot = 2, Type = "Flying" });
        db.SpeciesEggGroups.AddRange(
            new SpeciesEggGroup { SpeciesId = 6, EggGroup = "Monster" },
            new SpeciesEggGroup { SpeciesId = 6, EggGroup = "Dragon" });
        db.SpeciesNames.AddRange(
            new SpeciesName { SpeciesId = 6, Language = "en", Name = "Charizard" },
            new SpeciesName { SpeciesId = 6, Language = "ja", Name = "リザードン" });

        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = Now,
            LastSeenAt = Now,
        });
        db.Cards.Add(new Card
        {
            Id = 630417,
            SetId = 1,
            Url = "/game/pokemon-base-set/charizard-4",
            Name = "Charizard #4",
            FirstSeenAt = Now,
            LastSeenAt = Now,
        });

        await db.SaveChangesAsync();
    }
}
