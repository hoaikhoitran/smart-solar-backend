using MailKit.Security;
using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// The TLS mode for every SMTP setting. TLS is always negotiated: the implicit-TLS port connects with TLS, every other port requires
/// STARTTLS (a server without it fails), and only the explicit AllowInsecureTransport opt-in may continue without TLS
/// (StartTlsWhenAvailable). SecureSocketOptions.None is never used.
/// </summary>
public class SmtpSecurityTests
{
    [Theory]
    [InlineData(true, 465, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(false, 465, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(true, 587, false, SecureSocketOptions.StartTls)]
    [InlineData(false, 587, false, SecureSocketOptions.StartTls)]
    [InlineData(true, 25, false, SecureSocketOptions.StartTls)]
    [InlineData(false, 25, false, SecureSocketOptions.StartTls)]
    [InlineData(false, 1025, true, SecureSocketOptions.StartTlsWhenAvailable)]   // the explicit opt-in (local mail catcher)
    [InlineData(false, 465, true, SecureSocketOptions.SslOnConnect)]            // the opt-in still uses TLS on the implicit-TLS port
    public void Resolves_the_tls_mode_for_each_setting(bool useSsl, int port, bool allowInsecure, SecureSocketOptions expected)
        => Assert.Equal(expected, SmtpSecurity.Resolve(useSsl, port, allowInsecure));
}
