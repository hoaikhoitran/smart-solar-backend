namespace SmartSolar.Modules.Identity.Contracts.Persistence;

/// <summary>
/// Raised when a write loses the race against the unique email index, so the
/// module can answer with the same duplicate result as the pre-check.
/// </summary>
public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException(Exception innerException)
        : base("An account with this email already exists.", innerException)
    {
    }
}
