using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Modules.Identity.Contracts.Security;

public interface IPasswordHashingService
{
    string HashPassword(UserAccount userAccount, string password);

    /// <summary>
    /// True when the password matches the stored hash. Returns false rather
    /// than throwing for a missing or unreadable stored hash.
    /// </summary>
    bool VerifyPassword(UserAccount userAccount, string storedHash, string password);
}
