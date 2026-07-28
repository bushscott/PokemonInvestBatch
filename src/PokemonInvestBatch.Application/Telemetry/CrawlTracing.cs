using System.Diagnostics;

namespace PokemonInvestBatch.Application.Telemetry;

/// <summary>Span source for the crawl pipeline; exported via OTLP at the host.</summary>
public static class CrawlTracing
{
    public const string SourceName = "PokemonInvestBatch";

    public static readonly ActivitySource Source = new(SourceName);
}
