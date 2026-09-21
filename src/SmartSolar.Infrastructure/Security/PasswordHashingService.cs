using Microsoft.AspNetCore.Identity;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Security;

/// <summary>
/// Wraps ASP.NET Core's PasswordHasher. No custom password cryptography.
/// </summary>
public sealed class PasswordHashingService : IPasswordHashingService
{
    private readonly PasswordHasher<UserAccount> _hasher = new();

    public string HashPassword(UserAccount userAccount, string password)
        => _hasher.HashPassword(userAccount, password);

    public bool VerifyPassword(UserAccount userAccount, string storedHash, string password)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        try
        {
            var result = _hasher.VerifyHashedPassword(userAccount, storedHash, password);

            // SuccessRehashNeeded still means the password was correct.
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A stored value that is not a valid hash cannot match anything.
            return false;
        }
    }
}
