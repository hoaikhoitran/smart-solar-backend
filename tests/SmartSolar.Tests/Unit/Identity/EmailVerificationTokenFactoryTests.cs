using System.Security.Cryptography;
using System.Text;
using SmartSolar.Modules.Identity.EmailVerification;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>Email verification tokens: 32 random url-safe bytes sent to the user; only the SHA-256 hex hash is stored and looked up.</summary>
public class EmailVerificationTokenFactoryTests
{
    private readonly EmailVerificationTokenFactory _factory = new();

    [Fact]
    public void Create_returns_a_url_safe_raw_token_of_32_bytes()
    {
        var token = _factory.Create();

        Assert.Equal(43, token.RawToken.Length);   // base64url of 32 bytes without padding
        Assert.Equal(token.RawToken, Uri.EscapeDataString(token.RawToken));
        Assert.DoesNotContain('=', token.RawToken);
    }

    [Fact]
    public void Create_returns_a_different_token_each_time()
        => Assert.Equal(50, Enumerable.Range(0, 50).Select(_ => _factory.Create().RawToken).Distinct().Count());

    [Fact]
    public void The_stored_hash_is_the_sha256_of_the_raw_token_and_reproducible_for_lookups()
    {
        var token = _factory.Create();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.RawToken))).ToLowerInvariant(), token.TokenHash);
        Assert.Equal(token.TokenHash, EmailVerificationTokenFactory.Hash(token.RawToken));
        Assert.DoesNotContain(token.RawToken, token.TokenHash);
        Assert.NotEqual(EmailVerificationTokenFactory.Hash("token-one"), EmailVerificationTokenFactory.Hash("token-two"));
    }
}
