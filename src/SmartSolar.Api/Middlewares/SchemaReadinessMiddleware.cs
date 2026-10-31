using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Infrastructure.Persistence;

namespace SmartSolar.Api.Middlewares;

/// <summary>
/// Whether the PostgreSQL schema contains every migration this build maps. The code maps columns that exist only after its
/// migrations, so running against an older schema would turn ordinary reads into 500s; instead every request answers
/// 503 SCHEMA_NOT_READY until the approved migrations are applied. Only PostgreSQL is checked (the SQLite test hosts build their
/// schema with EnsureCreated and have no migration history). Once ready, the state never goes back (migrations are not removed
/// at runtime); while not ready or unknown (database unreachable) it is re-checked at most every <see cref="RecheckInterval"/>.
/// </summary>
public sealed class SchemaReadiness(ISchemaMigrationProbe probe, ILogger<SchemaReadiness> logger, TimeProvider time)
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _pending = [];

    /// <summary>Pending migration ids from the last check (empty when ready or not checked).</summary>
    public IReadOnlyList<string> Pending => _pending;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready) return true;
        if (time.GetUtcNow() - _checkedAt < RecheckInterval) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready || time.GetUtcNow() - _checkedAt < RecheckInterval) return _ready;
            _checkedAt = time.GetUtcNow();
            var pending = await probe.GetPendingMigrationsAsync(cancellationToken);
            if (pending is null)
            {
                _ready = true;
                return true;
            }

            _pending = pending;
            _ready = pending.Count == 0;
            if (!_ready)
                logger.LogCritical("Database schema is missing {Count} migration(s): {Migrations}. Requests answer 503 SCHEMA_NOT_READY until they are applied.",
                    pending.Count, string.Join(", ", pending));
            return _ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not check the database schema; requests answer 503 SCHEMA_NOT_READY until the check succeeds.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Lists the migrations of this build that the database has not applied; null when the provider has no migrations (SQLite tests).</summary>
public interface ISchemaMigrationProbe
{
    Task<IReadOnlyList<string>?> GetPendingMigrationsAsync(CancellationToken cancellationToken);
}

public sealed class EfSchemaMigrationProbe(IServiceScopeFactory scopes) : ISchemaMigrationProbe
{
    public async Task<IReadOnlyList<string>?> GetPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true) return null;
        return (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
    }
}

/// <summary>
/// Answers 503 SCHEMA_NOT_READY (enveloped) while <see cref="SchemaReadiness"/> is not ready, for every request, including the PayOS
/// webhook: a payment must never be applied to an outdated schema, PayOS retries the delivery and the reconciliation sweep confirms it
/// later. Also serves the probes:
/// <list type="bullet">
/// <item><c>GET /health/live</c>: 200 while the process runs (no database call), so an orchestrator does not restart a pod that only waits for migrations.</item>
/// <item><c>GET /health/ready</c>: 200 when the schema is current, otherwise the same 503 SCHEMA_NOT_READY, so traffic is not routed to it.</item>
/// </list>
/// </summary>
public sealed class SchemaReadinessMiddleware(RequestDelegate next, SchemaReadiness readiness)
{
    public const string ErrorCode = "SCHEMA_NOT_READY";
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.Equals(LivePath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(context, "live");
            return;
        }
        var ready = await readiness.IsReadyAsync(context.RequestAborted);
        if (ready && path.Equals(ReadyPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(context, "ready");
            return;
        }
        if (ready)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = ((int)SchemaReadiness.RecheckInterval.TotalSeconds).ToString();
        var envelope = ApiResponse<object>.Failure(new ApiError(ErrorCode,
                "The service is not ready: the database schema is older than this version. Apply the approved migrations.",
                new { pendingMigrations = readiness.Pending }),
            context.GetTraceId());
        await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task WriteStatusAsync(HttpContext context, string status)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { status }));
    }
}
