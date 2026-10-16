using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>Administrative account queries and writes. Never selects password hashes or token hashes into results.</summary>
/// <param name="includeInstallationShifts">Installation Flow enabled: installation shift assignments also block deactivation.</param>
public sealed class AdminUserStore(AppDbContext db, bool includeInstallationShifts = false) : IAdminUserStore
{
    public async Task<PagedResult<AdminUserRow>> ListAsync(AdminUserQuery query, CancellationToken cancellationToken)
    {
        var users = db.UserAccounts.AsNoTracking();
        if (!query.IncludeDeleted) users = users.Where(u => u.DeletedAt == null);
        if (query.Status is { } status) users = users.Where(u => u.Status == status);
        if (query.RoleCode is { } role) users = users.Where(u => u.UserRoles.Any(r => r.Role.Code == role));
        if (query.Search is { } search)
        {
            var term = search.ToLowerInvariant();
            users = users.Where(u => u.Email.ToLower().Contains(term) || u.FullName.ToLower().Contains(term)
                                     || (u.Phone != null && u.Phone.Contains(search)));
        }

        var total = await users.CountAsync(cancellationToken);

        users = (query.Sort, query.Descending) switch
        {
            (AdminUserSort.Name, false) => users.OrderBy(u => u.FullName).ThenBy(u => u.Id),
            (AdminUserSort.Name, true) => users.OrderByDescending(u => u.FullName).ThenBy(u => u.Id),
            (AdminUserSort.Status, false) => users.OrderBy(u => u.Status).ThenBy(u => u.FullName).ThenBy(u => u.Id),
            (AdminUserSort.Status, true) => users.OrderByDescending(u => u.Status).ThenBy(u => u.FullName).ThenBy(u => u.Id),
            (_, false) => users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id),
            _ => users.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id),
        };

        var page = await users.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(u => new { u.Id, u.Email, u.FullName, u.Phone, u.Status, u.EmailVerifiedAt, u.LastLoginAt, u.CreatedAt, u.UpdatedAt, u.DeletedAt, HasPassword = u.PasswordHash != null })
            .ToListAsync(cancellationToken);

        var ids = page.Select(u => u.Id).ToList();
        var roles = await RoleMapAsync(ids, cancellationToken);
        return PagedResult<AdminUserRow>.Create(
            page.Select(u => new AdminUserRow(u.Id, u.Email, u.FullName, u.Phone, u.Status, roles.GetValueOrDefault(u.Id) ?? [],
                u.EmailVerifiedAt, u.LastLoginAt, u.CreatedAt, u.UpdatedAt, u.DeletedAt, u.HasPassword)).ToList(),
            query.Page, query.PageSize, total);
    }

    public async Task<AdminUserRow?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var u = await db.UserAccounts.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.Id, x.Email, x.FullName, x.Phone, x.Status, x.EmailVerifiedAt, x.LastLoginAt, x.CreatedAt, x.UpdatedAt, x.DeletedAt, HasPassword = x.PasswordHash != null })
            .FirstOrDefaultAsync(cancellationToken);
        if (u is null) return null;
        var roles = await RoleMapAsync([u.Id], cancellationToken);
        return new AdminUserRow(u.Id, u.Email, u.FullName, u.Phone, u.Status, roles.GetValueOrDefault(u.Id) ?? [],
            u.EmailVerifiedAt, u.LastLoginAt, u.CreatedAt, u.UpdatedAt, u.DeletedAt, u.HasPassword);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> RoleMapAsync(List<Guid> ids, CancellationToken cancellationToken)
        => (await db.UserRoles.AsNoTracking().Where(r => ids.Contains(r.UserId)).Select(r => new { r.UserId, r.Role.Code }).ToListAsync(cancellationToken))
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Code).Order(StringComparer.Ordinal).ToList());

    public async Task<TechnicianProfileSummary?> GetTechnicianSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var p = await db.TechnicianProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        return p is null
            ? new TechnicianProfileSummary(false, false, false, null, null, false, null)
            : new TechnicianProfileSummary(true, p.IsSchedulingActive, p.BaseLatitude != null && p.BaseLongitude != null, p.BaseLabel,
                p.MaxAppointmentsPerDay, p.HasCustomWorkingSchedule, p.EarliestBaseDeparture);
    }

    public async Task<AccountOperationalWork> GetOperationalWorkAsync(Guid userId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var appointments = (await db.TechnicianAppointments.AsNoTracking()
                .Where(a => a.Status != AppointmentStatuses.Cancelled && a.EndsAt > nowUtc
                            && (a.ReservedTechnicianUserId == userId || a.AssignedTechnicianUserId == userId))
                .Select(a => new { a.Id, a.SurveyRequestId, a.StartsAt, a.Status })
                .ToListAsync(cancellationToken))
            .OrderBy(a => a.StartsAt)
            .Select(a => new BlockingAppointment(a.Id, a.SurveyRequestId, a.StartsAt, a.Status))
            .ToList();
        if (includeInstallationShifts)
        {
            // Reported with status INSTALLATION_SHIFT; the id is the installation shift assignment.
            appointments.AddRange((await db.InstallationShiftAssignments.AsNoTracking()
                    .Where(a => a.TechnicianUserId == userId && a.Status == "ACTIVE" && a.EndsAt > nowUtc)
                    .Select(a => new { a.Id, a.StartsAt })
                    .ToListAsync(cancellationToken))
                .OrderBy(a => a.StartsAt)
                .Select(a => new BlockingAppointment(a.Id, null, a.StartsAt, "INSTALLATION_SHIFT")));
        }

        var requests = await db.SurveyRequests.AsNoTracking()
            .Where(s => s.AssignedSaleId == userId
                        && (s.Status == SurveyRequestStatus.Assigned || s.Status == SurveyRequestStatus.Reviewing || s.Status == SurveyRequestStatus.Scheduled))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        return new AccountOperationalWork(appointments, requests);
    }

    public Task<UserAccount?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken)
        => db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<bool> EmailTakenAsync(string normalizedEmail, Guid? exceptUserId, CancellationToken cancellationToken)
        => db.UserAccounts.AsNoTracking().AnyAsync(u => u.Email == normalizedEmail && (exceptUserId == null || u.Id != exceptUserId), cancellationToken);

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsAsync(IEnumerable<string> roleCodes, CancellationToken cancellationToken)
    {
        var codes = roleCodes.ToList();
        return await db.Roles.AsNoTracking().Where(r => codes.Contains(r.Code)).ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken)
        => await db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).Select(r => r.Role.Code).ToListAsync(cancellationToken);

    public async Task LockAdministratorsAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return;  // SQLite: BEGIN IMMEDIATE already serialises writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM role WHERE code = {RoleCodes.Admin} FOR UPDATE", cancellationToken);
    }

    public async Task LockTechnicianSchedulingAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM technician_profile WHERE user_id = {userId} FOR UPDATE", cancellationToken);
    }

    public Task<int> CountOtherActiveAdminsAsync(Guid exceptUserId, CancellationToken cancellationToken)
        => db.UserAccounts.AsNoTracking()
            .CountAsync(u => u.Id != exceptUserId && u.Status == UserStatus.Active && u.DeletedAt == null
                             && u.UserRoles.Any(r => r.Role.Code == RoleCodes.Admin), cancellationToken);

    public async Task ReplaceRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.UserRoles.Where(r => r.UserId == userId).ToListAsync(cancellationToken);
        db.UserRoles.RemoveRange(existing.Where(r => !roleIds.Contains(r.RoleId)));
        foreach (var roleId in roleIds.Where(id => existing.All(r => r.RoleId != id)))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId, AssignedAt = now, AssignedBy = actorUserId });
        }
    }

    public async Task RevokeActiveRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var token in await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken))
        {
            token.RevokedAt = now;
        }
    }

    public async Task RemoveUnusedActionTokensAsync(Guid userId, AuthActionTokenType? type, CancellationToken cancellationToken)
        => db.AuthActionTokens.RemoveRange(await db.AuthActionTokens
            .Where(t => t.UserId == userId && t.UsedAt == null && (type == null || t.Type == type))
            .ToListAsync(cancellationToken));

    private static bool IsDuplicateEmail(DbUpdateException ex)
        => DbExceptionClassifier.PostgresDetails(ex) is ("23505", "IX_user_account_email")
           || DbExceptionClassifier.IsSqliteUniqueOn(ex, "user_account.email");

    public void AddUser(UserAccount user) => db.UserAccounts.Add(user);

    public void AddUserRole(UserRole userRole) => db.UserRoles.Add(userRole);

    public void AddActionToken(AuthActionToken token) => db.AuthActionTokens.Add(token);

    public void AddAudit(UserAccountAuditEntry entry) => db.UserAccountAuditEntries.Add(entry);

    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(cancellationToken, async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var (result, commit) = await work(token);
            if (commit)
            {
                try
                {
                    await db.SaveChangesAsync(token);
                }
                catch (DbUpdateException ex) when (IsDuplicateEmail(ex))
                {
                    throw new DuplicateEmailException(ex);
                }
                await transaction.CommitAsync(token);
            }
            else
            {
                await transaction.RollbackAsync(token);
            }
            db.ChangeTracker.Clear();
            return result;
        });
    }
}
