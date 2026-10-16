using SmartSolar.Infrastructure.Persistence;

namespace SmartSolar.Tests.TestSupport;

/// <summary>Helpers for racing callers against a disposable PostgreSQL database (each caller on its own context).</summary>
public static class PostgresRace
{
    /// <summary>Runs <paramref name="count"/> callers at once, each on its own context.</summary>
    public static async Task<T[]> RaceAsync<T>(PostgresTestDatabase database, int count, Func<AppDbContext, int, Task<T>> action)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await using var db = database.NewContext();
            await gate.Task;
            return await action(db, i);
        }).ToList();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static async Task<(PostgresTestDatabase Database, InstallationWorkflowTestContext Ctx)> WorldAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        return (database, InstallationWorkflowTestContext.CreateOn(database.NewContext()));
    }
}
