using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Modules.Identity.Contracts.Security;

/// <summary>An issued access token and the moment it stops being valid.</summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface IAccessTokenService
{
    AccessToken Generate(UserAccount userAccount, IReadOnlyCollection<string> roleCodes);
}
