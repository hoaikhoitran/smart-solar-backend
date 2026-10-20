using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Installations;

public sealed record PaidContractSource(Guid ContractId, string ContractNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid PropertySiteId, decimal? Latitude, decimal? Longitude, decimal TotalAmount, DateTimeOffset? PaidAt);

/// <summary>Where the work happens and whom to contact (shown to the responsible technician and staff only).</summary>
public sealed record InstallationSiteInfo(Guid PropertySiteId, string PropertyName, string Province, string? District, string? Ward, string? StreetLine,
    decimal? Latitude, decimal? Longitude, string CustomerName, string? CustomerPhone);

public interface IInstallationUnitOfWork : ITransactionalUnitOfWork
{
    Task<PaidContractSource?> GetContractAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>PAID contracts without any installation project (a cancelled project still counts: one project per contract).</summary>
    Task<IReadOnlyList<PaidContractSource>> ListPaidContractsWithoutProjectAsync(CancellationToken cancellationToken);
    Task<InstallationProject?> FindProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListProjectsAsync(string? status, CancellationToken cancellationToken);

    /// <summary>Projects where the technician has a current (PENDING/ACCEPTED) or COMPLETED assignment.</summary>
    Task<IReadOnlyList<InstallationProject>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<InstallationSiteInfo?> GetSiteAsync(Guid propertySiteId, Guid customerUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstallationAssignment>> GetAssignmentsAsync(Guid projectId, bool track, CancellationToken cancellationToken);

    /// <summary>The project's PENDING or ACCEPTED assignment, or null.</summary>
    Task<InstallationAssignment?> GetCurrentAssignmentAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationEvidence>> GetEvidenceAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationAcceptance>> GetAcceptancesAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstalledAsset>> GetAssetsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationChecklistItem>> GetChecklistAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationEvent>> GetEventsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken);
    Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
    Task<ZoneSummary?> GetZoneSummaryAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>Same technician-profile row lock as every survey booking/acceptance/assignment (sorted ids; PostgreSQL only).</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
}
