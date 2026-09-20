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
