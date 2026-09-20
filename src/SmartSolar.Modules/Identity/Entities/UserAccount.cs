using System;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.Entities;
public sealed class UserAccount
{
    public Guid Id { get; set; }

    public string Email { get; set; } = null!;

    public string? Phone { get; set; }

    public string? PasswordHash { get; set; }

    public string FullName { get; set; } = null!;

    public Guid? AvatarFileId { get; set; }

    public UserStatus Status { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; }
        = new List<UserRole>();
}