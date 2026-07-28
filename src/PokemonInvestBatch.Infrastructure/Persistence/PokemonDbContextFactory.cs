using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for `dotnet ef` commands. Generating migrations never
/// connects; applying them uses POKEMON_DB (the owner role's connection string)
/// so the app role itself never holds DDL rights.
/// </summary>
public class PokemonDbContextFactory : IDesignTimeDbContextFactory<PokemonDbContext>
{
    public PokemonDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POKEMON_DB")
            ?? "Host=localhost;Database=pokemon;Username=pokemon_owner";

        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new PokemonDbContext(options);
    }
}
