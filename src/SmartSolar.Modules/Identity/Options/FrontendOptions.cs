namespace SmartSolar.Modules.Identity.Options;

/// <summary>Non-secret settings bound from "Frontend".</summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    public string EmailVerificationUrl { get; set; } = string.Empty;

    public string PasswordResetUrl { get; set; } = string.Empty;
}
