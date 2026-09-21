using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Infrastructure.Security;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Tests.Unit.Identity;

public class SecureTokenFactoryTests
{
    private readonly SecureTokenFactory _factory = new();

    [Fact]
    public void Create_returns_a_url_safe_raw_token_of_32_bytes()
    {
        var token = _factory.Create();

        Assert.Equal(43, token.RawToken.Length);
        Assert.Equal(token.RawToken, Uri.EscapeDataString(token.RawToken));
    }

    [Fact]
    public void Create_returns_a_different_token_each_time()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => _factory.Create().RawToken).ToList();

        Assert.Equal(50, tokens.Distinct().Count());
    }

    [Fact]
    public void Hash_is_deterministic_and_not_the_raw_token()
    {
        var token = _factory.Create();

        Assert.Equal(token.TokenHash, SecureTokenFactory.Hash(token.RawToken));
        Assert.NotEqual(token.RawToken, token.TokenHash);
        Assert.Equal(64, token.TokenHash.Length);
    }
}

public class PasswordHashingServiceTests
{
    private static UserAccount User() => new()
    {
        Id = Guid.NewGuid(),
        Email = "person@example.com",
        FullName = "Test Person",
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Verify_accepts_the_password_that_produced_the_hash()
    {
        var service = new PasswordHashingService();
        var user = User();
        var hash = service.HashPassword(user, "correct horse battery");

        Assert.True(service.VerifyPassword(user, hash, "correct horse battery"));
    }

    [Fact]
    public void Verify_rejects_a_wrong_password()
    {
        var service = new PasswordHashingService();
        var user = User();
        var hash = service.HashPassword(user, "correct horse battery");

        Assert.False(service.VerifyPassword(user, hash, "wrong password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-hash")]
    public void Verify_rejects_unusable_stored_hashes_without_throwing(string storedHash)
    {
        var service = new PasswordHashingService();

        Assert.False(service.VerifyPassword(User(), storedHash, "any password"));
    }
}

public class JwtAccessTokenServiceTests
{
    private const string SigningKey = "test-signing-key-with-at-least-32-bytes!!";

    private static JwtOptions Options(int lifetimeMinutes = 15) => new()
    {
        Issuer = "SmartSolar",
        Audience = "SmartSolarClients",
        SigningKey = SigningKey,
        AccessTokenLifetimeMinutes = lifetimeMinutes
    };

    private static UserAccount User() => new()
    {
        Id = Guid.NewGuid(),
        Email = "person@example.com",
        FullName = "Test Person",
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static JwtSecurityToken Read(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void Generates_a_token_carrying_subject_email_and_roles()
    {
        var user = User();

        var issued = new JwtAccessTokenService(Options()).Generate(user, new[] { "CUSTOMER" });

        var jwt = Read(issued.Token);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(user.Email, jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("CUSTOMER", jwt.Claims.Single(c => c.Type == ClaimTypes.Role).Value);
    }

    [Fact]
    public void Includes_every_role_the_user_holds()
    {
        var issued = new JwtAccessTokenService(Options()).Generate(User(), new[] { "CUSTOMER", "ADMIN" });

        var roles = Read(issued.Token).Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        Assert.Equal(new[] { "CUSTOMER", "ADMIN" }, roles);
    }

    [Fact]
    public void Carries_no_password_material_or_unnecessary_claims()
    {
        var user = User();
        user.PasswordHash = "SUPER-SECRET-HASH";

        var issued = new JwtAccessTokenService(Options()).Generate(user, new[] { "CUSTOMER" });

        Assert.DoesNotContain("SUPER-SECRET-HASH", issued.Token);
        var claimTypes = Read(issued.Token).Claims.Select(c => c.Type).Distinct().ToList();
        Assert.All(claimTypes, type => Assert.Contains(type, new[]
        {
            JwtRegisteredClaimNames.Sub,
            JwtRegisteredClaimNames.Email,
            JwtRegisteredClaimNames.Jti,
            ClaimTypes.Role,
            "exp", "iss", "aud", "nbf", "iat"
        }));
    }

    [Fact]
    public void Expires_after_the_configured_lifetime()
    {
        var before = DateTimeOffset.UtcNow;

        var issued = new JwtAccessTokenService(Options(lifetimeMinutes: 30)).Generate(User(), Array.Empty<string>());

        Assert.InRange(issued.ExpiresAt, before.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
        Assert.InRange(
            Read(issued.Token).ValidTo,
            before.AddMinutes(29).UtcDateTime,
            DateTimeOffset.UtcNow.AddMinutes(31).UtcDateTime);
    }

    [Fact]
    public void Token_validates_against_the_configured_issuer_audience_and_key()
    {
        var options = Options();
        var issued = new JwtAccessTokenService(options).Generate(User(), new[] { "CUSTOMER" });

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            issued.Token,
            new TokenValidationParameters
            {
                ValidIssuer = options.Issuer,
                ValidAudience = options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(options.SigningKey)),
                ValidateIssuerSigningKey = true
            },
            out _);

        Assert.NotNull(principal);
    }

    [Fact]
    public void Token_signed_with_another_key_is_rejected()
    {
        var issued = new JwtAccessTokenService(Options()).Generate(User(), Array.Empty<string>());

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(
            issued.Token,
            new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = new SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes("a-completely-different-signing-key!!!!!!")),
                ValidateIssuerSigningKey = true
            },
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Refuses_to_construct_with_an_unusable_signing_key(string signingKey)
    {
        var options = Options();
        options.SigningKey = signingKey;

        var ex = Assert.Throws<InvalidOperationException>(() => new JwtAccessTokenService(options));
        // Names the setting, never the key material itself.
        Assert.Contains("Jwt:SigningKey", ex.Message);

        if (signingKey.Length > 0)
        {
            Assert.DoesNotContain(signingKey, ex.Message);
        }
    }

    [Fact]
    public void Refuses_a_non_positive_lifetime()
    {
        var options = Options(lifetimeMinutes: 0);

        Assert.Throws<InvalidOperationException>(() => new JwtAccessTokenService(options));
    }
}
