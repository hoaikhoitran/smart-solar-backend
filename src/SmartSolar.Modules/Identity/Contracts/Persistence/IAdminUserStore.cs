using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.Contracts.Persistence;

public enum AdminUserSort
{
    CreatedAt = 1,
    Name = 2,
    Status = 3
}

/// <summary>Filters and paging for the admin account list. Paging is applied in the database.</summary>
public sealed record AdminUserQuery(
    int Page,
    int PageSize,
    string? Search,
    string? RoleCode,
    UserStatus? Status,
    bool IncludeDeleted,
    AdminUserSort Sort,
    bool Descending);

/// <summary>Account row for administration. Contains no credential or token data.</summary>
public sealed record AdminUserRow(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    UserStatus Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset? EmailVerifiedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    bool HasPassword);

/// <summary>Safe technician scheduling summary shown on an account.</summary>
public sealed record TechnicianProfileSummary(
    bool HasProfile,
    bool IsSchedulingActive,
    bool HasBaseLocation,
    string? BaseLabel,
    int? MaxAppointmentsPerDay,
    bool HasCustomWorkingSchedule,
    TimeOnly? EarliestBaseDeparture);

/// <summary>An upcoming appointment that would be orphaned by disabling the account.</summary>
public sealed record BlockingAppointment(Guid AppointmentId, Guid? SurveyRequestId, DateTimeOffset StartsAt, string Status);

/// <summary>Operational work owned by an account that must be resolved before it stops working.</summary>
public sealed record AccountOperationalWork(
    IReadOnlyList<BlockingAppointment> TechnicianAppointments,
    IReadOnlyList<Guid> OwnedOpenSurveyRequestIds)
{
    public bool HasTechnicianWork => TechnicianAppointments.Count > 0;
    public bool HasSalesWork => OwnedOpenSurveyRequestIds.Count > 0;
}

/// <summary>Persistence boundary for administrative account management.</summary>
public interface IAdminUserStore
{
    Task<PagedResult<AdminUserRow>> ListAsync(AdminUserQuery query, CancellationToken cancellationToken);

    Task<AdminUserRow?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<TechnicianProfileSummary?> GetTechnicianSummaryAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Upcoming technician appointments and open survey requests owned by the account.</summary>
    Task<AccountOperationalWork> GetOperationalWorkAsync(Guid userId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Tracked account (including soft-deleted ones).</summary>
    Task<UserAccount?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> EmailTakenAsync(string normalizedEmail, Guid? exceptUserId, CancellationToken cancellationToken);

    /// <summary>Role ids by code, for the requested codes that exist.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsAsync(IEnumerable<string> roleCodes, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Serialises every operation that could remove the last active administrator (row lock on
    /// the ADMIN role in PostgreSQL; SQLite already serialises writers).
    /// </summary>
    Task LockAdministratorsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Row-locks the user's technician scheduling profile (if any) — the same lock every booking,
    /// assignment and reschedule takes — so an account change and a reservation for that technician
    /// are serialised: either the booking commits first and the change sees the work, or the change
    /// commits first and the booking no longer sees an eligible technician.
    /// </summary>
    Task LockTechnicianSchedulingAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Active, non-deleted holders of ADMIN other than <paramref name="exceptUserId"/>.</summary>
    Task<int> CountOtherActiveAdminsAsync(Guid exceptUserId, CancellationToken cancellationToken);

    /// <summary>Replaces the user's role rows: removes missing codes, adds new ones with AssignedBy.</summary>
    Task ReplaceRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken);

    Task RevokeActiveRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    Task RemoveUnusedActionTokensAsync(Guid userId, AuthActionTokenType? type, CancellationToken cancellationToken);

    void AddUser(UserAccount user);

    void AddUserRole(UserRole userRole);

    void AddActionToken(AuthActionToken token);

    void AddAudit(UserAccountAuditEntry entry);

    /// <summary>
    /// One transaction inside the retrying execution strategy; commits only when the work returns
    /// Commit = true. Throws <see cref="DuplicateEmailException"/> on a unique email violation.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken);
}
