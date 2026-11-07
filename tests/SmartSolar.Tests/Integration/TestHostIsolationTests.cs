using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration;

/// <summary>
/// Guards the HTTP test host against the cause of the intermittent 401/500 failures seen in loaded full runs:
/// every <see cref="AuthApiFactory"/> host shares one in-memory SQLite connection, so anything running beside the
/// test's own requests (a background worker started with the host) makes two DbContexts use that connection at once.
/// </summary>
public sealed class TestHostIsolationTests
{
    [Fact]
    public void Test_hosts_start_no_background_workers()
    {
        using var factory = new AuthApiFactory();

        var workers = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace == "SmartSolar.Api.Background")
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(workers);
    }

    [Fact]
    public void A_second_context_on_the_shared_connection_fails_while_a_statement_is_active()
    {
        using var factory = new AuthApiFactory();
        using var holder = factory.CreateDbContext();
        var connection = (SqliteConnection)holder.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        // What a request (for example the token re-check) does while another statement is still running.
        var error = Assert.Throws<SqliteException>(() =>
        {
            using var concurrent = factory.CreateDbContext();
            _ = concurrent.UserAccounts.Count();
        });

        Assert.Equal(5, error.SqliteErrorCode);
    }
}
