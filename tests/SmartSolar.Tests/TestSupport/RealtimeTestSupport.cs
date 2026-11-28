using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Realtime;
using SmartSolar.Modules.Common.Realtime;

namespace SmartSolar.Tests.TestSupport;

/// <summary>Records what the post-commit hook publishes; can throw or hang to test failure isolation.</summary>
public sealed class RecordingRealtimePublisher : IRealtimeNotificationPublisher
{
    public ConcurrentQueue<RealtimeNotification> Published { get; } = new();
    public int Calls;
    public bool Throw { get; set; }
    public TimeSpan? Hang { get; set; }
    /// <summary>Called on every publication (e.g. to check what the database holds at that moment).</summary>
    public Action<IReadOnlyList<RealtimeNotification>>? OnPublish { get; set; }

    public async Task PublishAsync(IReadOnlyList<RealtimeNotification> notifications, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        OnPublish?.Invoke(notifications);
        if (Throw) throw new InvalidOperationException("synthetic hub failure");
        if (Hang is { } hang) await Task.Delay(hang, CancellationToken.None);   // ignores the token on purpose: the hook must still return
        foreach (var n in notifications) Published.Enqueue(n);
    }

    public IReadOnlyList<RealtimeNotification> Snapshot() => Published.ToList();
    public void Clear() { while (Published.TryDequeue(out _)) { } }
}

/// <summary>Business contexts whose DbContext carries the production post-commit realtime interceptor.</summary>
public static class RealtimeTestSupport
{
    public static RealtimeNotificationInterceptor Interceptor(RecordingRealtimePublisher recorder, RealtimeOptions? options = null)
        => new(recorder, options ?? new RealtimeOptions { Enabled = true }, NullLogger<RealtimeNotificationInterceptor>.Instance);

    /// <summary>An in-memory SQLite database (schema created) whose context publishes through <paramref name="recorder"/>.</summary>
    public static AppDbContext Sqlite(RecordingRealtimePublisher recorder, RealtimeOptions? options = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .AddInterceptors(Interceptor(recorder, options))
            .Options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>A context on a disposable PostgreSQL database (same retrying strategy as production) with the interceptor.</summary>
    public static AppDbContext Postgres(PostgresTestDatabase database, RealtimeNotificationInterceptor interceptor)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null))
            .AddInterceptors(interceptor)
            .Options);
}
