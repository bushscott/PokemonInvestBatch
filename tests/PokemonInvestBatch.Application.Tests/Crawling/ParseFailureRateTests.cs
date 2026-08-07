using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Tests.Crawling;

/// <summary>
/// The corpus-wide "the parser broke" alarm. Most of these tests are about the
/// sample floor rather than the threshold, because the floor is what keeps a
/// freshly restarted worker from crying wolf off a handful of visits.
/// </summary>
public class ParseFailureRateTests
{
    private const double FivePercent = 0.05;

    [Fact]
    public void A_small_window_is_never_a_spike_even_when_everything_failed()
    {
        Assert.False(ParseFailureRate.IsSpike(parseFailures: 19, observed: 19, FivePercent));
    }

    [Fact]
    public void An_empty_window_is_not_a_spike()
    {
        // Also the divide-by-zero guard: a brand-new database has no visits.
        Assert.False(ParseFailureRate.IsSpike(parseFailures: 0, observed: 0, FivePercent));
    }

    [Fact]
    public void At_the_sample_floor_the_rate_starts_counting()
    {
        Assert.True(ParseFailureRate.IsSpike(parseFailures: 2, observed: 20, FivePercent));
    }

    [Fact]
    public void Exactly_at_the_threshold_is_not_yet_a_spike()
    {
        // 5 of 100 is exactly 5%; the rule fires above the line, not on it.
        Assert.False(ParseFailureRate.IsSpike(parseFailures: 5, observed: 100, FivePercent));
    }

    [Fact]
    public void Just_past_the_threshold_is_a_spike()
    {
        Assert.True(ParseFailureRate.IsSpike(parseFailures: 6, observed: 100, FivePercent));
    }

    [Fact]
    public void A_clean_window_is_never_a_spike()
    {
        Assert.False(ParseFailureRate.IsSpike(parseFailures: 0, observed: 100, FivePercent));
    }

    [Fact]
    public void The_threshold_is_the_callers_to_set()
    {
        // The same window is or is not a spike depending on configuration.
        Assert.False(ParseFailureRate.IsSpike(parseFailures: 10, observed: 100, threshold: 0.5));
        Assert.True(ParseFailureRate.IsSpike(parseFailures: 10, observed: 100, threshold: 0.01));
    }
}
