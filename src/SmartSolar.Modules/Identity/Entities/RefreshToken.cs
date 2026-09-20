using System;

namespace SmartSolar.Modules.Identity.Entities;

/// <summary>
/// Refresh token used to obtain a new access token. Only the hash is stored.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public UserAccount User { get; set; } = null!;
}
