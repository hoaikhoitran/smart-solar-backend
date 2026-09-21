using MailKit;
using MailKit.Net.Smtp;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Builds and configures the MailKit client. Settings here must be in place
/// before connecting, which is why construction and configuration live together.
/// <para>
/// Only certificate <em>revocation</em> checking is configurable. Chain, trust,
/// expiry and hostname validation stay at MailKit's defaults, and no
/// ServerCertificateValidationCallback is ever installed, so an invalid or
/// untrusted certificate still fails the connection.
/// </para>
/// </summary>
public static class SmtpClientSettings
{
    /// <summary>
    /// The only way the sender obtains a client, so a client can never be
    /// created without its settings.
    /// </summary>
    public static SmtpClient CreateClient(SmtpOptions smtp)
    {
        var client = new SmtpClient();

        try
        {
            Apply(client, smtp);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    public static void Apply(MailService client, SmtpOptions smtp)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(smtp);

        // .NET treats an unknown revocation status as a chain error, so this is
        // hard-fail: when the CRL/OCSP endpoint cannot be reached the handshake
        // fails with "An incomplete certificate revocation check occurred".
        // There is no soft-fail middle setting; false removes the check entirely.
        client.CheckCertificateRevocation = smtp.CheckCertificateRevocation;

        // Note: MailKit's ProxyClient carries its own CheckCertificateRevocation.
        // No proxy is configured today; if one is added, set it there too.
    }

    /// <summary>
    /// Describes the risk taken on when revocation checking is off, or null
    /// when it is on. Contains no credentials.
    /// </summary>
    public static string? DescribeRevocationRisk(SmtpOptions smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);

        if (smtp.CheckCertificateRevocation)
        {
            return null;
        }

        return "SMTP certificate revocation checking is disabled: a revoked but unexpired "
            + "certificate would be accepted. Chain, trust, expiry and hostname validation "
            + "still apply, and the connection is still encrypted.";
    }
}
