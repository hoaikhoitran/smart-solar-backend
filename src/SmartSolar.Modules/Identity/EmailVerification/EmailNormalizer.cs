namespace SmartSolar.Modules.Identity.EmailVerification;

/// <summary>
/// Single definition of the stored email format, shared by registration,
/// lookups and resend so they always agree.
/// </summary>
public static class EmailNormalizer
{
    public static string Normalize(string email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();
}
