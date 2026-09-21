namespace SmartSolar.Modules.Identity.Options;

/// <summary>Non-secret settings bound from "Auth:RefreshToken".</summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "Auth:RefreshToken";

    public int LifetimeDays { get; set; } = 14;
}
