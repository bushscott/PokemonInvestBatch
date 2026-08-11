using Microsoft.EntityFrameworkCore;
using Npgsql;
using PokemonInvestBatch.Infrastructure.Persistence;
using Xunit;

namespace PokemonInvestBatch.TestSupport;

/// <summary>
/// A base class for tests that need real PostgreSQL: each test gets its own
/// database, created and migrated before it runs and dropped after.
///
/// The suite used to share one fixed <c>pokemon_test</c> and truncate it
/// between tests, which made every test in the repo a hazard to every other:
/// two classes — or two test assemblies — running at once would delete each
/// other's fixtures halfway through an assertion, and the failure looked like
/// a bug in the code under test rather than in the harness. Guarding that with
/// a no-parallelism rule works only as long as everyone remembers it, and it
/// makes the suite slower forever to work around a harness limitation.
///
/// Building a database per test costs a second or two of migration and buys
/// back honest isolation: nothing leaks between tests, nothing has to run in a
/// particular order, and two suites can run side by side.
///
/// <c>POKEMON_TEST_DB</c> supplies the host and credentials. The database it
/// names is only a template — it is never written to.
/// </summary>
public abstract class DatabaseTest : IAsyncLifetime
{
    private string _databaseName = "";

    /// <summary>Null when POKEMON_TEST_DB is unset, which is how tests know to
    /// skip rather than fail on a machine with no database.</summary>
    public static string? Template => Environment.GetEnvironmentVariable("POKEMON_TEST_DB");

    public static bool Available => !string.IsNullOrWhiteSpace(Template);

    /// <summary>Points at this test's own database. Empty when skipping.</summary>
    protected string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        // A name no other run can collide with, so a crashed run leaves an
        // obvious orphan rather than corrupting the next one.
        _databaseName = $"pokemon_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(Maintenance()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(Template)
        {
            Database = _databaseName,
        }.ConnectionString;

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_databaseName.Length == 0)
        {
            return;
        }

        // Npgsql keeps pooled connections open, and PostgreSQL will not drop a
        // database anyone is still attached to.
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Maintenance());
        await admin.OpenAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Plain DROP asks only that our own connections be gone, which
            // ClearAllPools just arranged. WITH (FORCE) terminates a straggler
            // instead, and is the one PostgreSQL can refuse — 42501 when a
            // backend belongs to a role we are not a member of, which two
            // suites running side by side can produce. So it is the fallback,
            // not what we lead with: teardown of a scratch database must never
            // be the reason a passing test reports failure.
            if (await TryDropAsync(admin, force: false) || await TryDropAsync(admin, force: true))
            {
                return;
            }

            NpgsqlConnection.ClearAllPools();
            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
        }

        // Out of tries. Leave it: the name is a GUID, so the orphan is inert
        // and `DROP DATABASE` over `pokemon_test_%` sweeps it up later — the
        // same bargain this class already makes for a crashed run.
    }

    private async Task<bool> TryDropAsync(NpgsqlConnection admin, bool force)
    {
        try
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\"{(force ? " WITH (FORCE)" : "")}", admin);
            await drop.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException e) when (
            e.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
        {
            return false;
        }
    }

    protected PokemonDbContext NewContext() => new(ContextOptions());

    protected DbContextOptions<PokemonDbContext> ContextOptions() =>
        new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    /// <summary>CREATE DATABASE cannot run inside the database being created,
    /// so administration goes through the always-present <c>postgres</c>.</summary>
    private static string Maintenance() =>
        new NpgsqlConnectionStringBuilder(Template) { Database = "postgres" }.ConnectionString;
}
