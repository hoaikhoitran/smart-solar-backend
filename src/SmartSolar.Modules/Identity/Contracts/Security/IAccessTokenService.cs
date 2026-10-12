using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.Contracts.Security;

/// <summary>An issued access token and the moment it stops being valid.</summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The access-token claim carrying <see cref="UserAccount.AuthVersion"/>. A token whose value no
/// longer matches the account is rejected, so status changes, role changes and soft deletes take
/// effect immediately instead of when the token expires.
/// </summary>
public static class AuthVersionClaim
{
    public const string Type = "av";
}

/// <summary>Current authorization facts of an account, read on every authenticated request.</summary>
public sealed record AccountAuthorizationState(UserStatus Status, bool IsDeleted, int AuthVersion);

/// <summary>Per-request check that a validly signed token still describes an allowed session.</summary>
public interface IAccessTokenRevocationCheck
{
    /// <summary>
    /// True when the account exists, is ACTIVE, not deleted, and its auth version equals the
    /// token's (a token without the claim counts as version 0).
    /// </summary>
    Task<bool> IsCurrentAsync(Guid userId, int tokenAuthVersion, CancellationToken cancellationToken);
}

public interface IAccessTokenService
{
    AccessToken Generate(UserAccount userAccount, IReadOnlyCollection<string> roleCodes);
}
