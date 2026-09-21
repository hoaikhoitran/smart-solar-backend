using MailKit.Security;

namespace SmartSolar.Infrastructure.Email;

public static class SmtpSecurity
{
    private const int ImplicitTlsPort = 465;

    /// <summary>
    /// Chooses the transport security mode.
    /// <para>
    /// Port 465 always negotiates TLS on connect. Every other port requires
    /// STARTTLS, so a server that does not offer it fails the connection rather
    /// than sending credentials and the verification link in the clear.
    /// </para>
    /// <para>
    /// <paramref name="allowInsecureTransport"/> is the only way to reach a
    /// plaintext-capable mode, and it exists for local mail catchers such as
    /// Mailpit. Certificate validation is never disabled.
    /// </para>
    /// </summary>
    public static SecureSocketOptions Resolve(bool useSsl, int port, bool allowInsecureTransport)
    {
        if (port == ImplicitTlsPort)
        {
            return SecureSocketOptions.SslOnConnect;
        }

        if (allowInsecureTransport)
        {
            return SecureSocketOptions.StartTlsWhenAvailable;
        }

        // UseSsl only selects the negotiation style; TLS itself is not optional.
        return SecureSocketOptions.StartTls;
    }
}
