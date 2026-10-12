namespace SmartSolar.Modules.Identity.Options;

/// <summary>Non-secret settings bound from "Frontend".</summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    public string EmailVerificationUrl { get; set; } = string.Empty;

    public string PasswordResetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Page where an admin-provisioned user sets the first password. Optional: when empty the
    /// password reset page is used, because it posts to the same reset endpoint, which also
    /// accepts account-setup tokens.
    /// </summary>
    public string AccountSetupUrl { get; set; } = string.Empty;

    public string EffectiveAccountSetupUrl => string.IsNullOrWhiteSpace(AccountSetupUrl) ? PasswordResetUrl : AccountSetupUrl;
}
