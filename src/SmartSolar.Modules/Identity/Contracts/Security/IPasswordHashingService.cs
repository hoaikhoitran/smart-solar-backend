using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Modules.Identity.Contracts.Security;

public interface IPasswordHashingService
{
    string HashPassword(UserAccount userAccount, string password);
}
