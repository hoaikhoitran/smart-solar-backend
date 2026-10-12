namespace SmartSolar.Modules.Identity.Options;

/// <summary>Lifetime of the one-time link an admin-provisioned user opens to set the first password.</summary>
public sealed class AccountSetupOptions
{
    public const string SectionName = "Auth:AccountSetup";

    /// <summary>Default three days: long enough for staff onboarding, still single-use and expiring.</summary>
    public int LifetimeMinutes { get; set; } = 4320;
}
