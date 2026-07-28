using System.IO.Compression;
using System.Reflection;

namespace PokemonInvestBatch.Domain.Tests.Fixtures;

/// <summary>
/// Loads real pricecharting.com captures committed as gzipped embedded resources.
/// Captured 2026-07-27; the Wayback fixtures preserve older schema generations
/// (e.g. the pre-2026 <c>{"pop":[...]}</c> population shape) for drift tests.
/// </summary>
public static class Fixture
{
    /// <summary>Loads a fixture by base name, e.g. <c>"charizard-live-a"</c>.</summary>
    public static string Load(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"PokemonInvestBatch.Domain.Tests.Fixtures.{name}.html.gz";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Fixture '{name}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }
}
