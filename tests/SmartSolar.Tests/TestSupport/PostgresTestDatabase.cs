using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Runs only when SMARTSOLAR_TEST_POSTGRES points at a disposable LOCAL PostgreSQL server
/// (host must be localhost or 127.0.0.1); otherwise the test is reported as skipped.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "SMARTSOLAR_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a disposable local PostgreSQL connection string to run.";
        }
    }
}

/// <summary>Creates a uniquely named database, applies every EF migration, and drops it on dispose.</summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnection;

    private PostgresTestDatabase(string adminConnection, string connectionString, string name)
    {
        _adminConnection = adminConnection;
        ConnectionString = connectionString;
        Name = name;
    }

    public string ConnectionString { get; }
    public string Name { get; }

    /// <param name="withPendingSchema">Asserts the Installation Flow + knowledge base tables exist (they are created by the migrations).</param>
    /// <param name="upToMigration">Stops after this migration (for migration Down tests); null applies every migration.</param>
    public static async Task<PostgresTestDatabase> CreateAsync(bool withPendingSchema = false, string? upToMigration = null)
    {
        var raw = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)
                  ?? throw new InvalidOperationException($"{PostgresFactAttribute.Variable} is not set.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        if (builder.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException("Refusing to run PostgreSQL tests against a non-local host.");

        var name = $"smartsolar_test_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        var admin = builder.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        builder.Database = name;
        var database = new PostgresTestDatabase(admin, builder.ConnectionString, name);
        await using var db = database.NewContext();
        // Every EF migration, including InstallationWorkflowAndKnowledgeBase (the Installation Flow and
        // knowledge-base tables with their manual exclusion constraint), applied only to this throwaway database.
        if (upToMigration is null) await db.Database.MigrateAsync();
        else await db.GetService<IMigrator>().MigrateAsync(upToMigration);
        if (withPendingSchema)
        {
            // Kept for callers written before the migration existed: the schema now comes from the migration.
            var exists = await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.file_reference') IS NOT NULL AS \"Value\"").SingleAsync();
            if (!exists) throw new InvalidOperationException("The InstallationWorkflowAndKnowledgeBase migration did not create the expected tables.");
        }
        return database;
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null))
        .Options);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
