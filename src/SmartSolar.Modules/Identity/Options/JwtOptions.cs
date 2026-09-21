namespace SmartSolar.Modules.Identity.Options;

/// <summary>
/// Bound from "Jwt". Issuer, Audience and the lifetime are non-secret;
/// SigningKey is a secret and must come from the environment.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HS256 needs at least 256 bits of key material.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenLifetimeMinutes { get; set; } = 15;
}
