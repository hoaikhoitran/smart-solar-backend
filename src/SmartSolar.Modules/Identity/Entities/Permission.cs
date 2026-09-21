using System;

namespace SmartSolar.Modules.Identity.Entities;

public class Permission
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Module { get; set; } = null!;

    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; }
        = new List<RolePermission>();
}
