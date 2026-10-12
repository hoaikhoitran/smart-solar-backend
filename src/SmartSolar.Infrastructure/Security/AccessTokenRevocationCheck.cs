using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Infrastructure.Security;

/// <summary>
/// One indexed primary-key read per authenticated request. Deliberately not cached: a cache
/// would let a suspended or demoted user keep acting until it expired, on every instance.
/// </summary>
public sealed class AccessTokenRevocationCheck(AppDbContext db) : IAccessTokenRevocationCheck
{
    public async Task<bool> IsCurrentAsync(Guid userId, int tokenAuthVersion, CancellationToken cancellationToken)
    {
        var state = await db.UserAccounts.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.DeletedAt, u.AuthVersion })
            .FirstOrDefaultAsync(cancellationToken);

        return state is not null
            && state.Status == UserStatus.Active
            && state.DeletedAt == null
            && state.AuthVersion == tokenAuthVersion;
    }
}
