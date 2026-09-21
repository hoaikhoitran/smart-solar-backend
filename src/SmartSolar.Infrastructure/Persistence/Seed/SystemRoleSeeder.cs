using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Seed;

/// <summary>
/// Inserts the fixed system roles when they are absent. Idempotent and safe to
/// run on every startup: existing roles are left untouched and never duplicated.
/// </summary>
public sealed class SystemRoleSeeder
{
    /// <summary>Arbitrary constant identifying this bootstrap's advisory lock.</summary>
    private const long AdvisoryLockKey = 727274001;

    private readonly AppDbContext _db;
    private readonly ILogger<SystemRoleSeeder> _logger;

    public SystemRoleSeeder(AppDbContext db, ILogger<SystemRoleSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql())
        {
            await InsertMissingRolesAsync(cancellationToken);
            return;
        }

        // role.code has no unique index, so two replicas starting together could
        // each insert the full set. The advisory lock serializes the bootstrap.
        var strategy = _db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(cancellationToken, async token =>
        {
            _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(token);

            await _db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})",
                token);

            await InsertMissingRolesAsync(token);

            await transaction.CommitAsync(token);
        });
    }

    private async Task InsertMissingRolesAsync(CancellationToken cancellationToken)
    {
        var existingCodes = await _db.Roles
            .AsNoTracking()
            .Select(r => r.Code)
            .ToListAsync(cancellationToken);

        var missing = RoleCodes.All
            .Where(code => !existingCodes.Contains(code))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var code in missing)
        {
            _db.Roles.Add(new Role
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = ToDisplayName(code)
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded {Count} missing system role(s): {Codes}.",
            missing.Count,
            string.Join(", ", missing));
    }

    private static string ToDisplayName(string code)
        => string.Concat(code[..1], code[1..].ToLowerInvariant());
}
