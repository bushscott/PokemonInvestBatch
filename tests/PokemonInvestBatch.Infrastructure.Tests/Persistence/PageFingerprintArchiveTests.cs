using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Infrastructure.Tests.Persistence;

/// <summary>
/// The alarm that cried wolf, pinned down. Archiving a fingerprint and alerting
/// on it are separate decisions: a page is novel whenever the card carries an
/// amount of data no other card has carried, which is common and harmless.
/// Only a name nothing in the archive can account for is the site moving.
/// </summary>
public class PageFingerprintArchiveTests : DatabaseTest, IDisposable
{
    private const string FullPage = """
        <script>
          VGPC.chart_data = {"graded": [], "used": []};
          VGPC.pop_data = {"cgc": {}, "psa": {}};
        </script>
        <div class="completed-auctions-graded"></div>
        <div class="completed-auctions-used"></div>
        """;

    private const string PageWithANewTier = """
        <script>
          VGPC.chart_data = {"grade-twenty-three": [], "graded": [], "used": []};
          VGPC.pop_data = {"cgc": {}, "psa": {}};
        </script>
        <div class="completed-auctions-graded"></div>
        <div class="completed-auctions-used"></div>
        """;

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private readonly RecordingAlerter _alerter = new();

    private readonly IncidentThrottle _throttle = new(TimeSpan.FromHours(6));

    [SkippableFact]
    public async Task A_quieter_card_is_archived_without_a_word()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The false alarm of 2026-08-07: an obscure promo with one price tier
        // and no census is a combination never seen before and news to nobody.
        // Ten of them in half an hour buried the Critical channel.
        var promo = """
            <script>
              VGPC.chart_data = {"used": []};
            </script>
            <div class="completed-auctions-used"></div>
            """;

        await RecordAsync(FullPage);
        await RecordAsync(promo);

        await using var db = NewContext();
        Assert.Equal(2, await db.Fingerprints.CountAsync());
        Assert.Empty(_alerter.Raised);
    }

    [SkippableFact]
    public async Task A_name_nothing_accounts_for_is_worth_an_alert()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await RecordAsync(FullPage);
        await RecordAsync(PageWithANewTier);

        var alert = Assert.Single(_alerter.Raised);
        Assert.Equal("New page element observed", alert.Subject);
        Assert.Contains("chart_data:grade-twenty-three", alert.Body);
    }

    [SkippableFact]
    public async Task A_new_element_announces_itself_once()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // A markup change lands on every card at once, and a thousand
        // identical emails is the same information as one.
        var sameTierQuieterCard = """
            <script>
              VGPC.chart_data = {"grade-twenty-three": [], "used": []};
            </script>
            <div class="completed-auctions-used"></div>
            """;

        await RecordAsync(FullPage);
        await RecordAsync(PageWithANewTier);
        await RecordAsync(sameTierQuieterCard);

        await using var db = NewContext();
        Assert.Equal(3, await db.Fingerprints.CountAsync());
        Assert.Single(_alerter.Raised);
    }

    [SkippableFact]
    public async Task The_census_schema_change_still_raises_the_alarm()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The regression this alarm exists for, on the real pages: the 2024
        // census block was {"pop":[...]} and is now {"psa","cgc"}. A census
        // column by a name no page uses today is the site moving, and must
        // survive every rule added to quiet the false alarms.
        await RecordAsync(Fixture.Load("charizard-live-a"));
        await RecordAsync(Fixture.Load("charizard-2024-06-pop-schema"));

        var alert = Assert.Single(_alerter.Raised);
        Assert.Contains("pop_data:pop", alert.Body);
    }

    [SkippableFact]
    public async Task A_fingerprint_seen_before_only_moves_its_last_seen()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        var first = DateTimeOffset.UtcNow;
        await RecordAsync(FullPage, first);
        await RecordAsync(FullPage, first.AddMinutes(5));

        await using var db = NewContext();
        var fingerprint = Assert.Single(await db.Fingerprints.ToListAsync());
        Assert.True(fingerprint.LastSeenAt > fingerprint.FirstSeenAt);
        Assert.Empty(_alerter.Raised);
    }

    [SkippableFact]
    public async Task The_very_first_page_is_archived_in_silence()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // An empty archive has nothing to be unfamiliar against, so every name
        // on the first page is new. That is the thousand-identical-emails
        // case rather than news.
        await RecordAsync(FullPage);

        await using var db = NewContext();
        Assert.Single(await db.Fingerprints.ToListAsync());
        Assert.Empty(_alerter.Raised);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fingerprintDirectory))
        {
            Directory.Delete(_fingerprintDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task RecordAsync(string html, DateTimeOffset? at = null)
    {
        await using var db = NewContext();
        var archive = new PageFingerprintArchive(_throttle, _alerter, _fingerprintDirectory);
        await archive.RecordAsync(
            db, "/game/set/card", html, at ?? DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();
    }
}
