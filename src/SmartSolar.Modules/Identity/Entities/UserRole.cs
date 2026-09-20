using System;

namespace SmartSolar.Modules.Identity.Entities;

public class UserRole
{
    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public Guid? AssignedBy { get; set; }

    public UserAccount User { get; set; } = null!;

    public Role Role { get; set; } = null!;
}
