using System;

namespace SmartSolar.Modules.Identity.Entities;

public class RolePermission
{
    public Guid RoleId { get; set; }

    public Guid PermissionId { get; set; }

    public Role Role { get; set; } = null!;

    public Permission Permission { get; set; } = null!;
}
