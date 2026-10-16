using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Installations;

public sealed record PaidContractSource(Guid ContractId, string ContractNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid PropertySiteId, decimal? Latitude, decimal? Longitude, decimal TotalAmount, DateTimeOffset? PaidAt);

public interface IInstallationUnitOfWork : ITransactionalUnitOfWork
{
    Task<PaidContractSource?> GetContractAsync(Guid contractId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaidContractSource>> ListPaidContractsWithoutProjectAsync(CancellationToken cancellationToken);
    Task<InstallationProject?> FindProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListProjectsAsync(string? status, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<(decimal? Latitude, decimal? Longitude)> GetSiteLocationAsync(Guid propertySiteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstallationTeamMember>> GetTeamAsync(Guid projectId, bool activeOnly, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationShift>> GetShiftsAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationShiftAssignment>> GetAssignmentsAsync(Guid projectId, bool activeOnly, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationMilestone>> GetMilestonesAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<MilestoneEvidence>> GetEvidenceAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationAcceptance>> GetAcceptancesAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstalledAsset>> GetAssetsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationChecklistItem>> GetChecklistAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationEvent>> GetEventsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken);
    Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Same technician-profile row lock as every survey booking/assignment (sorted ids; PostgreSQL only).</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
}
