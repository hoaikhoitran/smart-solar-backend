namespace SmartSolar.Modules.Identity.Constants;

/// <summary>
/// System-owned role codes. These are reference data, not user-created roles.
/// </summary>
public static class RoleCodes
{
    public const string Customer = "CUSTOMER";
    public const string Sales = "SALES";
    public const string Technician = "TECHNICIAN";
    public const string Manager = "MANAGER";
    public const string Admin = "ADMIN";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Customer,
        Sales,
        Technician,
        Manager,
        Admin
    };
}
