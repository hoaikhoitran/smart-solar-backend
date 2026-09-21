namespace SmartSolar.Modules.Identity.Options;

/// <summary>Non-secret settings bound from "Auth:PasswordReset".</summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "Auth:PasswordReset";

    public int LifetimeMinutes { get; set; } = 60;
}
