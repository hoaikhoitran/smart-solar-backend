using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class IdentityUnitOfWork : IIdentityUnitOfWork
{
    private const string PostgresUniqueViolation = "23505";
    private const int SqliteConstraintViolation = 19;

    private readonly AppDbContext _db;

    public IdentityUnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken)
        => _db.UserAccounts
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalizedEmail, cancellationToken);

    public async Task<Guid?> FindRoleIdByCodeAsync(string roleCode, CancellationToken cancellationToken)
    {
        var id = await _db.Roles
            .AsNoTracking()
            .Where(r => r.Code == roleCode)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return id;
    }

    public Task<AccountVerificationState?> FindAccountForResendAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
        => _db.UserAccounts
            .AsNoTracking()
            .Where(u => u.Email == normalizedEmail)
            .Select(u => new AccountVerificationState(
                u.Id,
                u.Email,
                u.FullName,
                u.EmailVerifiedAt != null,
                u.PasswordHash != null))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<UserAccount?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
        => _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<UserAccount?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => _db.UserAccounts.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRoleCodesAsync(
        Guid userId,
        CancellationToken cancellationToken)
        => await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Code)
            .OrderBy(code => code)
            .ToListAsync(cancellationToken);

    public Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
        => _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public void AddRefreshToken(RefreshToken refreshToken) => _db.RefreshTokens.Add(refreshToken);

    public async Task<bool> TryRevokeRefreshTokenAsync(
        Guid refreshTokenId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        // The WHERE clause carries the "still unrevoked" condition, so two
        // concurrent rotations cannot both win.
        var updated = await _db.RefreshTokens
            .Where(t => t.Id == refreshTokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.RevokedAt, revokedAt),
                cancellationToken);

        return updated == 1;
    }

    public async Task RevokeActiveRefreshTokensAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in active)
        {
            token.RevokedAt = revokedAt;
        }
    }

    public Task<AuthActionToken?> FindActionTokenAsync(
        string tokenHash,
        AuthActionTokenType type,
        CancellationToken cancellationToken)
        => _db.AuthActionTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Type == type, cancellationToken);

    public async Task RemoveUnusedTokensAsync(
        Guid userId,
        AuthActionTokenType type,
        CancellationToken cancellationToken)
    {
        var stale = await _db.AuthActionTokens
            .Where(t => t.UserId == userId && t.Type == type && t.UsedAt == null)
            .ToListAsync(cancellationToken);

        _db.AuthActionTokens.RemoveRange(stale);
    }

    public void AddUserAccount(UserAccount userAccount) => _db.UserAccounts.Add(userAccount);

    public void AddUserRole(UserRole userRole) => _db.UserRoles.Add(userRole);

    public void AddAuthActionToken(AuthActionToken token) => _db.AuthActionTokens.Add(token);

    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        // The provider may be configured with EnableRetryOnFailure, which forbids
        // user-initiated transactions unless they run inside the execution strategy.
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            cancellationToken,
            async (token) =>
            {
                // Each attempt starts from a clean slate so a rolled-back attempt
                // cannot re-insert its entities on the next try.
                _db.ChangeTracker.Clear();

                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                var result = await work(token);

                try
                {
                    await _db.SaveChangesAsync(token);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    throw new DuplicateEmailException(ex);
                }

                await transaction.CommitAsync(token);

                return result;
            });
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException switch
        {
            null => false,
            { } inner when inner.GetType().Name == "PostgresException"
                => GetProperty(inner, "SqlState") as string == PostgresUniqueViolation,
            { } inner when inner.GetType().Name == "SqliteException"
                => GetProperty(inner, "SqliteErrorCode") as int? == SqliteConstraintViolation
                    && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static object? GetProperty(Exception exception, string propertyName)
        => exception.GetType().GetProperty(propertyName)?.GetValue(exception);
}
