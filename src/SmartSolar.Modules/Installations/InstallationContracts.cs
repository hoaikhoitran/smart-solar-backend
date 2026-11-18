using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Installations;

/// <param name="PaymentPlan">Non-null for a 30/70 contract (ready for installation once SIGNED after its deposit; 100% is not required).</param>
public sealed record PaidContractSource(Guid ContractId, string ContractNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid PropertySiteId, decimal? Latitude, decimal? Longitude, decimal TotalAmount, DateTimeOffset? PaidAt, string? PaymentPlan = null)
{
    public bool ReadyForInstallation => CustomerContracts.ContractStatuses.IsReadyForInstallation(Status, PaymentPlan);
}

/// <summary>Where the work happens and whom to contact (shown to the responsible technician and staff only).</summary>
public sealed record InstallationSiteInfo(Guid PropertySiteId, string PropertyName, string Province, string? District, string? Ward, string? StreetLine,
    decimal? Latitude, decimal? Longitude, string CustomerName, string? CustomerPhone);

/// <summary>The catalog facts an installed asset is checked against.</summary>
public sealed record AssetCatalogProduct(Guid ProductId, string ProductType, int? WarrantyMonths);

/// <summary>A SOLAR_PANEL or INVERTER product and its quantity in the contract's accepted quotation.</summary>
public sealed record ContractedEquipment(Guid ProductId, string ProductType, string Name, decimal Quantity);

/// <summary>
/// A SIGNED or PAID contract as the installation schedule sees it. <see cref="SurveyTechnicianUserId"/> = the technician of the request's
/// latest COMPLETED on-site survey report (the technician responsible for agreeing the installation date); null when there is none.
/// </summary>
/// <param name="Prepaid">The contract was created from an installation quotation whose payment was verified (technician-led flow): paid before it was
/// signed. Such a contract can be scheduled at once; its schedule is confirmed when the signed contract becomes PAID.</param>
/// <param name="PaymentPlan">Non-null for a 30/70 contract: schedulable once its deposit is verified, confirmed when the paper contract is signed.</param>
public sealed record ScheduleContractInfo(Guid ContractId, string ContractNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId, Guid PropertySiteId,
    Guid? SurveyTechnicianUserId, string PropertyName, string CustomerName, bool Prepaid = false, string? PaymentPlan = null)
{
    /// <summary>Explicit readiness (never "status == PAID"): legacy PAID, or a 30/70 contract SIGNED / PAID.</summary>
    public bool ReadyForInstallation => CustomerContracts.ContractStatuses.IsReadyForInstallation(Status, PaymentPlan);
}

public interface IInstallationUnitOfWork : ITransactionalUnitOfWork, IInAppNotificationWriter
{
    // ---- Installation schedule proposals (technician-agreed dates) ----
    Task<ScheduleContractInfo?> GetScheduleContractAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>SIGNED / PAID contracts whose latest completed on-site survey was done by the technician.</summary>
    Task<IReadOnlyList<ScheduleContractInfo>> ListScheduleContractsForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken);
    Task<InstallationProject?> FindProjectByContractAsync(Guid contractId, CancellationToken cancellationToken);
    Task<InstallationScheduleProposal?> GetLiveProposalAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<InstallationScheduleProposal?> FindProposalAsync(Guid proposalId, bool track, CancellationToken cancellationToken);

    /// <summary>Proposals filtered by contract, technician, customer and status (each null = any); newest first.</summary>
    Task<IReadOnlyList<InstallationScheduleProposal>> ListProposalsAsync(Guid? contractId, Guid? technicianUserId, Guid? customerUserId, string? status,
        CancellationToken cancellationToken);

    /// <summary>PLANNED proposals whose contract is PAID (to be confirmed).</summary>
    Task<IReadOnlyList<Guid>> ListContractsWithPlannedProposalAndPaidAsync(int limit, CancellationToken cancellationToken);

    /// <summary>PLANNED proposals whose start has passed while the contract is not PAID yet.</summary>
    Task<IReadOnlyList<Guid>> ListExpiredPlannedProposalIdsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>Offers of the contract (null status = any), oldest start first.</summary>
    Task<IReadOnlyList<InstallationSlotOffer>> ListSlotOffersAsync(Guid contractId, string? status, bool track, CancellationToken cancellationToken);
    Task<InstallationSlotOffer?> FindSlotOfferAsync(Guid offerId, bool track, CancellationToken cancellationToken);

    /// <summary>Live schedules (PLANNED / CONFIRMED / RESCHEDULE_REQUIRED) of the technician on other contracts overlapping [startsAt, endsAt).</summary>
    Task<IReadOnlyList<InstallationScheduleProposal>> ListOverlappingLiveProposalsAsync(Guid technicianUserId, Guid exceptContractId, DateTimeOffset startsAt,
        DateTimeOffset endsAt, CancellationToken cancellationToken);

    /// <summary>Row lock on the contract (PostgreSQL only): serialises every schedule write and confirmation of one contract.</summary>
    Task LockContractAsync(Guid contractId, CancellationToken cancellationToken);

    Task<PaidContractSource?> GetContractAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>PAID contracts without any installation project (a cancelled project still counts: one project per contract).</summary>
    Task<IReadOnlyList<PaidContractSource>> ListPaidContractsWithoutProjectAsync(CancellationToken cancellationToken);
    Task<InstallationProject?> FindProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationProject>> ListProjectsAsync(string? status, CancellationToken cancellationToken);

    /// <summary>One page of projects (newest first, id as tie-breaker), status filtered in SQL, with the total.</summary>
    Task<(IReadOnlyList<InstallationProject> Rows, int Total)> PageProjectsAsync(string? status, int skip, int take, CancellationToken cancellationToken);

    /// <summary>One page of schedules (newest first, id as tie-breaker), status filtered in SQL, with the total.</summary>
    Task<(IReadOnlyList<InstallationScheduleProposal> Rows, int Total)> PageProposalsAsync(string? status, int skip, int take, CancellationToken cancellationToken);

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

    /// <summary>The customer's selected simulation summary of the survey request (null when none was selected).</summary>
    Task<SimulatedProductionView?> GetSelectedSimulationAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>Catalog facts of the given products that are not deleted (type and manufacturer warranty); missing ids are absent.</summary>
    /// <summary>Panels and inverters of the accepted quotation the contract was made from (its lines are immutable since issue).</summary>
    Task<IReadOnlyList<ContractedEquipment>> GetContractedEquipmentAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>The accepted quotation the contract was made from.</summary>
    Task<Guid> GetContractQuotationIdAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>The project's CUSTOMER_APPROVAL_PENDING or APPROVED substitution (at most one: partial unique index).</summary>
    Task<EquipmentSubstitution?> GetActiveSubstitutionAsync(Guid projectId, bool track, CancellationToken cancellationToken);

    Task<IReadOnlyList<EquipmentSubstitution>> ListSubstitutionsAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EquipmentSubstitutionItem>> GetSubstitutionItemsAsync(IReadOnlyCollection<Guid> substitutionIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, AssetCatalogProduct>> GetAssetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
    Task<ZoneSummary?> GetZoneSummaryAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>Same technician-profile row lock as every survey booking/acceptance/assignment (sorted ids; PostgreSQL only).</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
}
