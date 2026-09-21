using MailKit.Security;
using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

public class SmtpSecurityTests
{
    [Theory]
    // Implicit-TLS port always connects with TLS, even if UseSsl was left false.
    [InlineData(true, 465, SecureSocketOptions.SslOnConnect)]
    [InlineData(false, 465, SecureSocketOptions.SslOnConnect)]
    // Every other port requires STARTTLS; a server without it fails the connection.
    [InlineData(true, 587, SecureSocketOptions.StartTls)]
    [InlineData(false, 587, SecureSocketOptions.StartTls)]
    [InlineData(true, 25, SecureSocketOptions.StartTls)]
    [InlineData(false, 25, SecureSocketOptions.StartTls)]
    public void Resolves_the_expected_mode_for_each_setting(
        bool useSsl,
        int port,
        SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpSecurity.Resolve(useSsl, port, allowInsecureTransport: false));
    }

    [Fact]
    public void Tls_is_required_even_when_ssl_is_switched_off()
    {
        // StartTlsWhenAvailable would silently continue in plaintext; it must not be used here.
        Assert.NotEqual(
            SecureSocketOptions.StartTlsWhenAvailable,
            SmtpSecurity.Resolve(useSsl: false, port: 25, allowInsecureTransport: false));
    }

    [Fact]
    public void Insecure_transport_is_only_possible_through_the_explicit_opt_in()
    {
        Assert.Equal(
            SecureSocketOptions.StartTlsWhenAvailable,
            SmtpSecurity.Resolve(useSsl: false, port: 1025, allowInsecureTransport: true));
    }

    [Fact]
    public void The_opt_in_still_uses_tls_on_the_implicit_tls_port()
    {
        Assert.Equal(
            SecureSocketOptions.SslOnConnect,
            SmtpSecurity.Resolve(useSsl: false, port: 465, allowInsecureTransport: true));
    }

    [Theory]
    [InlineData(true, 465, false)]
    [InlineData(false, 25, false)]
    [InlineData(false, 1025, true)]
    public void Never_resolves_to_a_connection_that_skips_tls_negotiation(
        bool useSsl,
        int port,
        bool allowInsecure)
    {
        Assert.NotEqual(SecureSocketOptions.None, SmtpSecurity.Resolve(useSsl, port, allowInsecure));
    }
}
