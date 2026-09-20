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
}
