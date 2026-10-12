namespace SmartSolar.Modules.Identity.Entities;

/// <summary>
/// Append-only record of an administrative change to an account (creation, profile, status,
/// roles, soft delete, recovery). Never stores credentials, tokens or password material.
/// </summary>
public sealed class UserAccountAuditEntry
{
    public Guid Id { get; set; }

    public Guid TargetUserId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string Action { get; set; } = null!;

    /// <summary>Short, non-sensitive description (for example "ACTIVE -> SUSPENDED").</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
