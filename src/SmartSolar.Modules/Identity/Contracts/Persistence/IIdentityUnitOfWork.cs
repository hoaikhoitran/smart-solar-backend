using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.Contracts.Persistence;

/// <summary>
/// Persistence boundary for the Identity module. Implemented by Infrastructure
/// so module code never depends on EF Core types.
/// </summary>
public interface IIdentityUnitOfWork
{
    /// <summary>Read-only existence check; must not track the entity.</summary>
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Read-only role lookup returning the identifier only.</summary>
    Task<Guid?> FindRoleIdByCodeAsync(string roleCode, CancellationToken cancellationToken);

    /// <summary>Read-only lookup of the fields resend needs; must not track.</summary>
    Task<AccountVerificationState?> FindAccountForResendAsync(
        string normalizedEmail,
        CancellationToken cancellationToken);

    Task<UserAccount?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Tracked lookup used by login, which updates the account.</summary>
    Task<UserAccount?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Read-only role codes for the access token's role claims.</summary>
    Task<IReadOnlyList<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Tracked lookup of a refresh token by its hash.</summary>
    Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);

    void AddRefreshToken(RefreshToken refreshToken);

    /// <summary>
    /// Revokes one refresh token only if it is still unrevoked, in a single
    /// conditional statement. Returns false when another request revoked it
    /// first, which makes rotation single-use under concurrency.
    /// </summary>
    Task<bool> TryRevokeRefreshTokenAsync(
        Guid refreshTokenId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every refresh token for the user that is still usable. Used after a
    /// password reset or change so pre-existing tokens cannot mint access tokens.
    /// </summary>
    Task RevokeActiveRefreshTokensAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    Task<AuthActionToken?> FindActionTokenAsync(
        string tokenHash,
        AuthActionTokenType type,
        CancellationToken cancellationToken);

    /// <summary>Removes unused tokens of one type for one user. Other types are untouched.</summary>
    Task RemoveUnusedTokensAsync(
        Guid userId,
        AuthActionTokenType type,
        CancellationToken cancellationToken);

    void AddUserAccount(UserAccount userAccount);

    void AddUserRole(UserRole userRole);

    void AddAuthActionToken(AuthActionToken token);

    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction and commits it, using the
    /// provider's execution strategy so it stays retriable. The whole unit may run
    /// more than once, so all reads and writes belong inside the delegate; anything
    /// left over from a failed attempt is discarded before the next one.
    /// Throws <see cref="DuplicateEmailException"/> when the unique email index is violated.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken);
}

/// <summary>The account facts resend needs, without tracking the entity.</summary>
public sealed record AccountVerificationState(
    Guid UserId,
    string Email,
    string FullName,
    bool IsVerified,
    bool HasPassword);
