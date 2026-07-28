using PokemonInvestBatch.Application.Alerting;

namespace PokemonInvestBatch.Application.Tests.Alerting;

public class IncidentThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_report_of_an_incident_alerts()
    {
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));

        Assert.True(throttle.ShouldAlert("schema-drift:pop_data", Now));
    }

    [Fact]
    public void Repeats_within_the_window_stay_quiet()
    {
        // A site change fails thousands of pages; the user gets one email,
        // not ten thousand.
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));
        throttle.ShouldAlert("schema-drift:pop_data", Now);

        Assert.False(throttle.ShouldAlert("schema-drift:pop_data", Now.AddMinutes(90)));
    }

    [Fact]
    public void The_same_incident_alerts_again_after_the_window()
    {
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));
        throttle.ShouldAlert("schema-drift:pop_data", Now);

        Assert.True(throttle.ShouldAlert("schema-drift:pop_data", Now.AddHours(7)));
    }

    [Fact]
    public void Distinct_incidents_alert_independently()
    {
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));
        throttle.ShouldAlert("schema-drift:pop_data", Now);

        Assert.True(throttle.ShouldAlert("canary:charizard-4", Now.AddMinutes(1)));
    }
}
