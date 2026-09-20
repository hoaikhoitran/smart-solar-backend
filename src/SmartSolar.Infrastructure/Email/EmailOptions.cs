namespace SmartSolar.Infrastructure.Email;

/// <summary>Bound from the "Email" section. Secrets come from the environment.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; }

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Smart Solar";

    /// <summary>
    /// Allows a plaintext-capable connection for local mail catchers. Off by
    /// default; when on, TLS is used only if the server offers it.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>
    /// Whether the certificate's revocation status is checked. On by default,
    /// and hard-fail: if the CA's CRL/OCSP endpoint cannot be reached, the
    /// handshake fails rather than continuing unchecked.
    /// <para>
    /// Setting this to false removes the revocation check entirely (there is no
    /// soft-fail option), so a revoked but unexpired certificate would be
    /// accepted. Chain, trust, expiry and hostname validation and TLS
    /// encryption are unaffected.
    /// </para>
    /// </summary>
    public bool CheckCertificateRevocation { get; set; } = true;
}
