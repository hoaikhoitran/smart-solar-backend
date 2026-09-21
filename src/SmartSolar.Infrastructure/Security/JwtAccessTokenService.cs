using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Infrastructure.Security;

/// <summary>
/// Issues short-lived HS256 access tokens. The token carries only what the API
/// needs to authorize a request: subject, email and role codes.
/// </summary>
public sealed class JwtAccessTokenService : IAccessTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public JwtAccessTokenService(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Validated here so a misconfigured key fails at startup, not on first login.
        // The message names the setting, never the value.
        var keyBytes = Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty);

        if (keyBytes < JwtOptions.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"'{JwtOptions.SectionName}:SigningKey' must be at least "
                + $"{JwtOptions.MinimumSigningKeyBytes} bytes for HS256.");
        }

        if (options.AccessTokenLifetimeMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"'{JwtOptions.SectionName}:AccessTokenLifetimeMinutes' must be greater than zero.");
        }

        _options = options;
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!)),
            SecurityAlgorithms.HmacSha256);
    }

    public AccessToken Generate(UserAccount userAccount, IReadOnlyCollection<string> roleCodes)
    {
        ArgumentNullException.ThrowIfNull(userAccount);
        ArgumentNullException.ThrowIfNull(roleCodes);

        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddMinutes(_options.AccessTokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userAccount.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, userAccount.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        claims.AddRange(roleCodes.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _credentials);

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
