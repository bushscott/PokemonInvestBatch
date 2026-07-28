using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using PokemonInvestBatch.Application.Alerting;

namespace PokemonInvestBatch.Application.Tests.Alerting;

public class CriticalLogAlerterTests
{
    [Fact]
    public async Task Alerts_become_critical_structured_logs()
    {
        // Alert decisions live in New Relic; the app's job is a Critical log
        // that journald forwarding turns into a queryable NR Logs record.
        var logger = new FakeLogger<CriticalLogAlerter>();
        var alerter = new CriticalLogAlerter(logger);

        await alerter.RaiseAsync("Canary failed: /game/x", "details here", CancellationToken.None);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Critical, record.Level);
        Assert.Contains("Canary failed: /game/x", record.Message);
        Assert.Contains("details here", record.Message);
    }
}
