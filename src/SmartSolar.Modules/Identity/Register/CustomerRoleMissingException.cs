namespace SmartSolar.Modules.Identity.Register;

/// <summary>
/// Thrown when the system CUSTOMER role cannot be resolved. Registration fails
/// loudly rather than creating an account without its role.
/// </summary>
public sealed class CustomerRoleMissingException : Exception
{
    public CustomerRoleMissingException(string roleCode)
        : base($"Required system role '{roleCode}' was not found. Role bootstrap must run before registration.")
    {
    }
}
