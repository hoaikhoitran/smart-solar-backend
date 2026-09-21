using System.Security.Cryptography;
using System.Text;
using SmartSolar.Modules.Identity.EmailVerification;

namespace SmartSolar.Tests.Unit.Identity;

public class EmailVerificationTokenFactoryTests
{
    private readonly EmailVerificationTokenFactory _factory = new();

    [Fact]
    public void Create_returns_raw_token_that_is_url_safe()
    {
        var token = _factory.Create();

        Assert.DoesNotContain('+', token.RawToken);
        Assert.DoesNotContain('/', token.RawToken);
        Assert.DoesNotContain('=', token.RawToken);
        Assert.Equal(token.RawToken, Uri.EscapeDataString(token.RawToken));
    }

    [Fact]
    public void Create_uses_32_bytes_of_entropy()
    {
        var token = _factory.Create();

        // Base64url of 32 bytes is 43 characters once padding is stripped.
        Assert.Equal(43, token.RawToken.Length);
    }

    [Fact]
    public void Create_returns_a_different_raw_token_each_time()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => _factory.Create().RawToken).ToList();

        Assert.Equal(50, tokens.Distinct().Count());
    }

    [Fact]
    public void Create_returns_sha256_hash_of_raw_token()
    {
        var token = _factory.Create();

        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.RawToken))).ToLowerInvariant();
        Assert.Equal(expected, token.TokenHash);
    }

    [Fact]
    public void Create_does_not_return_the_raw_token_inside_the_hash()
    {
        var token = _factory.Create();

        Assert.DoesNotContain(token.RawToken, token.TokenHash);
    }

    [Fact]
    public void Hash_is_deterministic_so_incoming_tokens_can_be_looked_up()
    {
        var token = _factory.Create();

        Assert.Equal(token.TokenHash, EmailVerificationTokenFactory.Hash(token.RawToken));
    }

    [Fact]
    public void Hash_differs_for_different_tokens()
    {
        Assert.NotEqual(
            EmailVerificationTokenFactory.Hash("token-one"),
            EmailVerificationTokenFactory.Hash("token-two"));
    }
}
