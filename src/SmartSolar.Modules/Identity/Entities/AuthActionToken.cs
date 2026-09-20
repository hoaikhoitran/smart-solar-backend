using System;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.Entities;

/// <summary>
/// One-time authentication action token (email verification, password reset).
/// Not used for access/refresh/OAuth/session tokens or API keys.
/// </summary>
public class AuthActionToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = null!;

    public AuthActionTokenType Type { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public UserAccount User { get; set; } = null!;
}
