namespace SmartSolar.Modules.Identity.EmailVerification;

public static class VerificationUrlBuilder
{
    public static string Build(string baseUrl, string rawToken)
    {
        var separator = baseUrl.Contains('?') ? '&' : '?';

        return $"{baseUrl}{separator}token={Uri.EscapeDataString(rawToken)}";
    }
}
