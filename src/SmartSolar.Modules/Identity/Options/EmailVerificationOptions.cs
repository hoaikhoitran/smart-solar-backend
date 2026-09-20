namespace SmartSolar.Modules.Identity.Options;

/// <summary>Non-secret settings bound from "Auth:EmailVerification".</summary>
public sealed class EmailVerificationOptions
{
    public const string SectionName = "Auth:EmailVerification";

    public int LifetimeMinutes { get; set; } = 1440;
}
