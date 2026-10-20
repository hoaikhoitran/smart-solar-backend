using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Modules.Installations;

public sealed record AssetInput(string AssetType, Guid? ProductId, string Manufacturer, string Model, string SerialNumber, string? PositionCode, int? WarrantyMonths);
public sealed record ChecklistUpdate(bool IsRequired, string Status, Guid? EvidenceFileId, string? Note);

/// <summary>
/// Manager (re)assignment of the responsible technician for an interval. <see cref="Reason"/> is required for a reassignment or
/// reschedule, and for a cross-zone technician together with <see cref="CrossZoneAuthorized"/> = true.
/// </summary>
public sealed record AssignTechnicianInput(Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason, int ExpectedRevision,
    bool CrossZoneAuthorized = false);

public sealed class InstallationOptions
{
    public const string SectionName = "Installation";
    public int MaxPlanningHorizonDays { get; set; } = 365;

    /// <summary>Bootstrap default of <c>installation.maxAssignmentDays</c> (longest assignment, in local calendar days touched); read from the settings.</summary>
    public int MaxAssignmentDays { get; set; } = 14;

    /// <summary>A completion report needs at least one registered panel and one inverter.</summary>
    public bool RequirePanelAndInverterAssets { get; set; } = true;
}

/// <summary>
/// Installation project use cases with one responsible technician. Managers create projects from PAID contracts,
/// assign/reassign/reschedule the technician (shared eligibility, technician locks, buffer), cancel before work
/// starts and keep the pre-start checklist. The responsible technician accepts or declines, starts, adds verified
/// photo evidence and assets and reports completion. The customer accepts (handover) or requests rework.
/// Every transition is revision-checked inside one transaction and audited; every response is scoped to the caller's role.
/// </summary>
public sealed class InstallationHandlers(
    IInstallationUnitOfWork unitOfWork,
    ISchedulingUnitOfWork scheduling,
    InstallationSchedulePlanner planner,
    FileService files,
    WorkingCalendar calendar,
    InstallationOptions options,
    TimeProvider time)
{
    /// <summary>Change outcome meaning "already in the requested state": success without a write.</summary>
    private const string Noop = "NOOP";

    /// <summary>Internal outcome of an assignment whose survey-hold re-home plan grew under the locks: roll back and retry.</summary>
    private const string RehomeRetry = "REHOME_RETRY";

    /// <summary>Internal outcome: the site's zone changed before the FOR SHARE lock was taken; re-plan with the new zone.</summary>
    private const string CoverageRetry = "COVERAGE_RETRY";

    /// <summary>Bound on re-reads of a site coverage that keeps changing between the pre-read and the area locks.</summary>
    private const int CoverageAttempts = 3;

    // ================================================================== Manager / Admin

    public Task<IReadOnlyList<PaidContractSource>> ListReadyContractsAsync(CancellationToken cancellationToken)
        => unitOfWork.ListPaidContractsWithoutProjectAsync(cancellationToken);

    /// <summary>PAID contract → PENDING_ASSIGNMENT (one project per contract; unique index backstop).</summary>
    public async Task<OperationResult<InstallationStaffView>> CreateProjectAsync(Guid managerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null) return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.Status != CustomerContracts.ContractStatuses.Paid)
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ContractNotPaid, "Installation starts only after the full payment is verified.");

        var coverage = await scheduling.GetSiteCoverageAsync(contract.PropertySiteId, cancellationToken);
        var projectId = Guid.NewGuid();
        for (var attempt = 0; attempt < CoverageAttempts; attempt++)
        {
            var locked = coverage;
            bool created;
            try
            {
                created = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    // The site lock serialises with a site location change; the FOR SHARE zone lock (same as booking and
                    // assignment) with a zone deactivation or commune removal, which then either sees this project (and refuses) or commits first.
                    await scheduling.LockPropertySiteAsync(contract.PropertySiteId, token);
                    await scheduling.LockZonesAsync(locked.ZoneIds, shared: true, token);
                    var inLock = await scheduling.GetSiteCoverageAsync(contract.PropertySiteId, token);
                    // The zone changed before the locks were taken: retry with the new zone locked.
                    if (!SameZone(inLock, locked))
                    {
                        coverage = inLock;
                        return (false, false);
                    }
                    var now = time.GetUtcNow();
                    unitOfWork.Add(new InstallationProject
                    {
                        Id = projectId, ContractId = contractId, SurveyRequestId = contract.SurveyRequestId, CustomerUserId = contract.CustomerUserId,
                        PropertySiteId = contract.PropertySiteId, ManagerUserId = managerUserId, Status = InstallationStatuses.PendingAssignment,
                        OperationalZoneId = inLock.Zone?.Id, CreatedAt = now, UpdatedAt = now,
                    });
                    foreach (var (code, title) in DefaultInstallationPlan.Checklist)
                        unitOfWork.Add(new InstallationChecklistItem { Id = Guid.NewGuid(), ProjectId = projectId, Code = code, Title = title, IsRequired = false, Status = ChecklistStatuses.Pending });
                    Event(projectId, InstallationEventTypes.ProjectCreated, managerUserId, null, InstallationStatuses.PendingAssignment, $"contract {contract.ContractNumber}", now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
            }
            catch (PersistenceConflictException)
            {
                return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ProjectExists, "An installation project already exists for this contract.");
            }
            if (created) return OperationResult<InstallationStaffView>.Ok(await StaffViewAsync(projectId, cancellationToken));
        }
        return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.StaleRevision, "The site's operational zone kept changing. Try again.");
    }

    public async Task<IReadOnlyList<InstallationProjectSummary>> ListProjectsAsync(string? status, CancellationToken cancellationToken)
    {
        var projects = await unitOfWork.ListProjectsAsync(status, cancellationToken);
        var names = await unitOfWork.GetUserNamesAsync(projects.Where(p => p.ResponsibleTechnicianUserId is not null).Select(p => p.ResponsibleTechnicianUserId!.Value).Distinct().ToList(), cancellationToken);
        return projects.Select(p => new InstallationProjectSummary(p.Id, p.ContractId, p.Status, p.ResponsibleTechnicianUserId,
            p.ResponsibleTechnicianUserId is { } t ? names.GetValueOrDefault(t) : null, p.TechnicianAcceptedAt is not null,
            Local(p.ScheduledStartsAt), Local(p.ScheduledEndsAt), p.OperationalZoneId, p.CreatedAt, p.Revision)).ToList();
    }

    public async Task<OperationResult<InstallationStaffView>> GetForManagerAsync(Guid projectId, CancellationToken cancellationToken)
        => await unitOfWork.FindProjectAsync(projectId, false, cancellationToken) is null
            ? NotFound<InstallationStaffView>()
            : OperationResult<InstallationStaffView>.Ok(await StaffViewAsync(projectId, cancellationToken));

    /// <summary>
    /// Members of the site's operational zone first, then every other technician flagged <c>crossZone</c> (manual cross-zone dispatch:
    /// only the zone rule is skipped for them), each with whether they can take the interval (no booking is made). A site whose commune
    /// has no active zone lists only cross-zone candidates; SITE_LOCATION_REQUIRED / ZONE_NOT_SERVICEABLE still refuse.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<InstallationCandidateView>>> GetCandidatesAsync(Guid projectId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        CancellationToken cancellationToken)
    {
        if (await planner.ValidateIntervalAsync(startsAt, endsAt, time.GetUtcNow(), cancellationToken) is { } invalid)
            return OperationResult<IReadOnlyList<InstallationCandidateView>>.Fail(InstallationErrorCodes.InvalidSchedule, invalid);
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null) return NotFound<IReadOnlyList<InstallationCandidateView>>();
        var coverage = await CoverageAsync(project, cancellationToken, allowNoZone: true);
        if (!coverage.Succeeded) return coverage.As<IReadOnlyList<InstallationCandidateView>>();
        var zoneIds = coverage.Value!.ZoneIds;
        var current = await unitOfWork.GetCurrentAssignmentAsync(projectId, false, cancellationToken);
        var members = await scheduling.ListZoneMemberIdsAsync(zoneIds, cancellationToken);
        var others = (await scheduling.ListTechniciansAsync(null, cancellationToken)).Select(t => t.UserId).Except(members).ToList();
        var candidates = new List<InstallationCandidateView>(await planner.CandidatesAsync(members, startsAt, endsAt, zoneIds, Ignore(current), cancellationToken));
        candidates.AddRange(await planner.CandidatesAsync(others, startsAt, endsAt, null, Ignore(current), cancellationToken));
        return OperationResult<IReadOnlyList<InstallationCandidateView>>.Ok(candidates);
    }

    /// <summary>
    /// Assign, reassign or reschedule the responsible technician (PENDING_ASSIGNMENT, PENDING_TECHNICIAN_ACCEPTANCE, SCHEDULED →
    /// PENDING_TECHNICIAN_ACCEPTANCE; IN_PROGRESS/REWORK_REQUIRED keep their status). The previous current assignment becomes
    /// REPLACED and the new one is PENDING until the technician accepts. Eligibility is checked, then re-checked under the
    /// technician locks; the exclusion constraint is the database backstop.
    /// </summary>
    public async Task<OperationResult<InstallationStaffView>> AssignAsync(Guid managerUserId, Guid projectId, AssignTechnicianInput input, CancellationToken cancellationToken)
    {
        var startsAt = input.StartsAt.ToUniversalTime();
        var endsAt = input.EndsAt.ToUniversalTime();
        if (await planner.ValidateIntervalAsync(startsAt, endsAt, time.GetUtcNow(), cancellationToken) is { } invalid)
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.InvalidSchedule, invalid);
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null) return NotFound<InstallationStaffView>();
        if (project.Revision != input.ExpectedRevision)
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.",
                new { currentRevision = project.Revision });
        if (!InstallationStatuses.IsAssignable(project.Status))
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.InvalidState, $"A {project.Status} project cannot be (re)assigned.");
        var current = await unitOfWork.GetCurrentAssignmentAsync(projectId, false, cancellationToken);
        var reason = Trim(input.Reason, 500);
        if ((current is not null || InstallationStatuses.IsStarted(project.Status)) && reason is null)
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ReasonRequired, "A reason is required to reassign or reschedule.");
        if (current is not null && current.TechnicianUserId == input.TechnicianUserId && current.StartsAt == startsAt && current.EndsAt == endsAt)
            return OperationResult<InstallationStaffView>.Ok(await StaffViewAsync(projectId, cancellationToken));   // nothing to change

        var coverage = await CoverageAsync(project, cancellationToken, allowNoZone: true);
        if (!coverage.Succeeded) return coverage.As<InstallationStaffView>();
        var areas = coverage.Value!;
        // Manual cross-zone dispatch: a technician outside the site's zone needs an explicit authorization with a reason; it skips ONLY the
        // zone rule (account, scheduling flag, working hours, leave, buffer/overlap, existing assignments and the locks still apply).
        var crossZone = !await MemberOfAsync(input.TechnicianUserId, areas, cancellationToken);
        // Not a technician at all: the eligibility check below reports TECHNICIAN_INACTIVE (no authorization would help).
        var isTechnician = (await scheduling.GetTechnicianAsync(input.TechnicianUserId, cancellationToken)) is { HasTechnicianRole: true };
        if (crossZone && isTechnician && (!input.CrossZoneAuthorized || reason is null))
            return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.CrossZoneAuthorizationRequired,
                "The technician is not a member of the site's operational zone: set crossZoneAuthorized = true and give a reason.",
                new { technicianUserId = input.TechnicianUserId });
        var (name, ineligible, plan) = await planner.CheckAsync(input.TechnicianUserId, startsAt, endsAt, crossZone ? null : areas.ZoneIds, Ignore(current), cancellationToken);
        if (ineligible is not null) return Ineligible<InstallationStaffView>(input.TechnicianUserId, name, ineligible);

        var extraLocks = new HashSet<Guid>();
        var coverageRetries = 0;
        OperationResult<bool> result = Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
        for (var attempt = 0; attempt < 4 + CoverageAttempts; attempt++)
        {
            // The technician's own pending survey holds in the interval are handed to the other technicians who could take them: lock those too.
            var locks = new HashSet<Guid>(extraLocks) { input.TechnicianUserId };
            if (current is not null) locks.Add(current.TechnicianUserId);
            locks.UnionWith(plan!.CandidateTechnicianIds);
            var planned = plan;
            var lockedAreas = areas;
            result = await MutateAsync(projectId, input.ExpectedRevision, locks, async (p, now, token) =>
            {
                if (!InstallationStatuses.IsAssignable(p.Status)) return Fail(InstallationErrorCodes.InvalidState, $"A {p.Status} project cannot be (re)assigned.");
                var fresh = await unitOfWork.GetCurrentAssignmentAsync(projectId, true, token);
                if (fresh?.Id != current?.Id) return Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
                // Site lock (vs a site location change), then FOR SHARE on the zone (vs deactivation / commune removal); the coverage
                // read under those locks is the one written. When it differs from the planned zone, re-plan with the new zone locked.
                await scheduling.LockPropertySiteAsync(p.PropertySiteId, token);
                await scheduling.LockZonesAsync(lockedAreas.ZoneIds, shared: true, token);
                var inLock = await scheduling.GetSiteCoverageAsync(p.PropertySiteId, token);
                if (!SameZone(inLock, lockedAreas) || !(inLock.Status == CoverageStatuses.Covered || (crossZone && inLock.Status == CoverageStatuses.NoZoneCoverage)))
                {
                    areas = inLock;
                    return Fail(CoverageRetry, "retry with the new zone");
                }
                var (_, again, freshPlan) = await planner.CheckAsync(input.TechnicianUserId, startsAt, endsAt, crossZone ? null : inLock.ZoneIds, Ignore(fresh), token);
                if (again is not null) return OperationResult<bool>.Fail(again, IneligibleMessage(again), new { technicianUserId = input.TechnicianUserId });

                // The assignment being replaced leaves its technician in this transaction, so that technician may take over a hold.
                var (status, session) = await planner.Rehoming.DetachForRehomeAsync(planned, freshPlan!, locks, input.TechnicianUserId, Ignore(fresh), now, token);
                if (status == AppointmentAssignmentService.RehomeStatus.Retry)
                {
                    extraLocks.UnionWith(freshPlan!.CandidateTechnicianIds);
                    plan = freshPlan;
                    return Fail(RehomeRetry, "retry with more technicians locked");
                }
                if (status == AppointmentAssignmentService.RehomeStatus.Conflict)
                    return OperationResult<bool>.Fail(EligibilityReasons.ScheduleConflict,
                        "The technician has a pending survey appointment in this interval that no other technician can take over.",
                        new { technicianUserId = input.TechnicianUserId });

                var from = p.Status;
                if (fresh is not null)
                {
                    fresh.Status = AssignmentStatuses.Replaced;
                    fresh.ClosedAt = now;
                }
                p.ResponsibleTechnicianUserId = input.TechnicianUserId;
                p.TechnicianAcceptedAt = null;
                p.ScheduledStartsAt = startsAt;
                p.ScheduledEndsAt = endsAt;
                p.PlannedStartDate = calendar.LocalDateOf(startsAt);
                p.PlannedEndDate = calendar.LocalDateOf(endsAt.AddTicks(-1));
                p.OperationalZoneId = inLock.Zone?.Id;
                Guid? authorization = null;
                if (crossZone)
                {
                    var record = new Scheduling.Dispatch.CrossZoneAuthorization
                    {
                        Id = Guid.NewGuid(), InstallationProjectId = projectId, TechnicianUserId = input.TechnicianUserId, TargetZoneId = inLock.Zone?.Id,
                        Reason = reason!, AuthorizedByUserId = managerUserId, AuthorizedAt = now,
                    };
                    unitOfWork.Add(record);
                    authorization = record.Id;
                }
                if (!InstallationStatuses.IsStarted(p.Status)) p.Status = InstallationStatuses.PendingTechnicianAcceptance;
                // The project revision guard and the release of the old assignment go first (frees the unique index and the exclusion).
                await unitOfWork.SaveChangesAsync(token);

                unitOfWork.Add(new InstallationAssignment
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, TechnicianUserId = input.TechnicianUserId, StartsAt = startsAt, EndsAt = endsAt,
                    Status = AssignmentStatuses.Pending, AssignedByUserId = managerUserId, AssignedAt = now, AssignmentReason = reason,
                });
                var type = fresh is null ? InstallationEventTypes.TechnicianAssigned
                    : fresh.TechnicianUserId == input.TechnicianUserId ? InstallationEventTypes.Rescheduled : InstallationEventTypes.TechnicianReassigned;
                var detail = fresh is null || fresh.TechnicianUserId == input.TechnicianUserId
                    ? $"technician {input.TechnicianUserId}; {startsAt:O} – {endsAt:O}; {reason}"
                    : $"technician {fresh.TechnicianUserId} → {input.TechnicianUserId}; {startsAt:O} – {endsAt:O}; {reason}";
                if (authorization is { } authorizationId) detail = $"{detail}; cross-zone authorization {authorizationId}";
                if (session!.MovedHoldIds.Count > 0) detail = Trim($"{detail}; survey holds re-homed: {session.MovedHoldIds.Count}", 1000);
                Event(projectId, type, managerUserId, from, p.Status, detail, now);
                await unitOfWork.SaveChangesAsync(token);
                // The survey holds get their new holders (HOLD_MOVED events) in the same transaction.
                await planner.Rehoming.AttachAsync(session, input.TechnicianUserId, managerUserId, now, token);
                return OperationResult<bool>.Ok(true);
            }, cancellationToken);
            if (result.ErrorCode == CoverageRetry)
            {
                if (++coverageRetries >= CoverageAttempts) break;
                if (CoverageFailure<InstallationStaffView>(areas, allowNoZone: true) is { } coverageFailure) return coverageFailure;
                if (!crossZone && !await MemberOfAsync(input.TechnicianUserId, areas, cancellationToken))
                    return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.CrossZoneAuthorizationRequired,
                        "The site's operational zone changed and the technician is not a member of it: set crossZoneAuthorized = true and give a reason.",
                        new { technicianUserId = input.TechnicianUserId });
                (name, ineligible, plan) = await planner.CheckAsync(input.TechnicianUserId, startsAt, endsAt, crossZone ? null : areas.ZoneIds, Ignore(current), cancellationToken);
                if (ineligible is not null) return Ineligible<InstallationStaffView>(input.TechnicianUserId, name, ineligible);
                continue;
            }
            if (result.ErrorCode != RehomeRetry) break;
        }
        if (result.ErrorCode == RehomeRetry) result = Fail(InstallationErrorCodes.StaleRevision, "The technician's schedule kept changing. Reload and try again.");
        if (result.ErrorCode == CoverageRetry) result = Fail(InstallationErrorCodes.StaleRevision, "The site's operational zone kept changing. Reload and try again.");
        return await ThenAsync(result, () => StaffViewAsync(projectId, cancellationToken));
    }

    /// <summary>PENDING_ASSIGNMENT / PENDING_TECHNICIAN_ACCEPTANCE / SCHEDULED → CANCELLED (reason required). The contract and payment are untouched.</summary>
    public async Task<OperationResult<InstallationStaffView>> CancelAsync(Guid managerUserId, Guid projectId, int expectedRevision, string? reason, CancellationToken cancellationToken)
    {
        var why = Trim(reason, 500);
        if (why is null) return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ReasonRequired, "A cancellation reason is required.");
        var result = await MutateAsync(projectId, expectedRevision, null, async (p, now, token) =>
        {
            if (p.Status == InstallationStatuses.Cancelled) return OperationResult<bool>.Fail(Noop, "already cancelled");
            if (!InstallationStatuses.IsCancellable(p.Status))
                return Fail(InstallationErrorCodes.InvalidState, "Only projects that have not started can be cancelled.");
            var from = p.Status;
            if (await unitOfWork.GetCurrentAssignmentAsync(projectId, true, token) is { } current)
            {
                current.Status = AssignmentStatuses.Cancelled;
                current.ClosedAt = now;
            }
            p.Status = InstallationStatuses.Cancelled;
            p.CancelledAt = now;
            p.CancellationReason = why;
            p.ResponsibleTechnicianUserId = null;
            p.TechnicianAcceptedAt = null;
            Event(projectId, InstallationEventTypes.ProjectCancelled, managerUserId, from, p.Status, why, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => StaffViewAsync(projectId, cancellationToken));
    }

    /// <summary>
    /// Manager/Admin: PENDING_CUSTOMER_ACCEPTANCE → REWORK_REQUIRED without the customer (e.g. the report is incomplete or wrong).
    /// The pending acceptance round is closed as REWORK_REQUESTED with the manager as decider and the reason, exactly like a customer
    /// rework, so the next completion report needs new evidence. Completion itself stays a customer decision.
    /// </summary>
    public async Task<OperationResult<InstallationStaffView>> ReturnToReworkAsync(Guid managerUserId, Guid projectId, int expectedRevision, string? reason,
        CancellationToken cancellationToken)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is null) return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ReasonRequired, "A reason is required to return the installation to rework.");
        if (why.Length > 500) return OperationResult<InstallationStaffView>.Fail(CommonErrorCodes.ValidationFailed, "The reason must be at most 500 characters.");
        var result = await MutateAsync(projectId, expectedRevision, null, async (p, now, token) =>
        {
            if (p.Status != InstallationStatuses.PendingCustomerAcceptance)
                return Fail(InstallationErrorCodes.InvalidState, "Only an installation waiting for the customer's acceptance can be returned to rework.");
            var pending = (await unitOfWork.GetAcceptancesAsync(projectId, true, token)).SingleOrDefault(a => a.Decision == AcceptanceDecisions.Pending);
            if (pending is null) return Fail(InstallationErrorCodes.InvalidState, "There is no pending completion report.");
            pending.Decision = AcceptanceDecisions.ReworkRequested;
            pending.DecidedByUserId = managerUserId;
            pending.DecidedAt = now;
            pending.Reason = why;
            var from = p.Status;
            p.Status = InstallationStatuses.ReworkRequired;
            Event(projectId, InstallationEventTypes.ReturnedToRework, managerUserId, from, p.Status, $"round {pending.Round}: {why}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => StaffViewAsync(projectId, cancellationToken));
    }

    public async Task<OperationResult<InstallationStaffView>> UpdateChecklistItemAsync(Guid managerUserId, Guid projectId, Guid itemId, int revision, ChecklistUpdate update, CancellationToken cancellationToken)
    {
        var status = update.Status?.Trim().ToUpperInvariant();
        if (status is not (ChecklistStatuses.Pending or ChecklistStatuses.Satisfied or ChecklistStatuses.NotApplicable))
            return OperationResult<InstallationStaffView>.Fail(CommonErrorCodes.ValidationFailed, "Status must be PENDING, SATISFIED or NOT_APPLICABLE.");
        if (status == ChecklistStatuses.Satisfied)
        {
            if (update.EvidenceFileId is not { } fileId)
                return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.EvidenceRequired, "A verified document is required to mark an item satisfied.");
            var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.ProjectDocument, null, cancellationToken);
            if (!verified.Succeeded) return verified.As<InstallationStaffView>();
        }
        var result = await MutateAsync(projectId, revision, null, async (project, now, token) =>
        {
            if (InstallationStatuses.IsFinal(project.Status)) return Fail(InstallationErrorCodes.InvalidState, $"{project.Status} projects are final.");
            var item = (await unitOfWork.GetChecklistAsync(projectId, true, token)).FirstOrDefault(i => i.Id == itemId);
            if (item is null) return Fail(InstallationErrorCodes.ChecklistItemNotFound, "Checklist item was not found.");
            item.IsRequired = update.IsRequired;
            item.Status = status!;
            item.EvidenceFileId = status == ChecklistStatuses.Satisfied ? update.EvidenceFileId : null;
            item.Note = Trim(update.Note, 1000);
            item.VerifiedByUserId = status == ChecklistStatuses.Pending ? null : managerUserId;
            item.VerifiedAt = status == ChecklistStatuses.Pending ? null : now;
            Event(projectId, InstallationEventTypes.ChecklistUpdated, managerUserId, null, null, $"{item.Code}: required={item.IsRequired}, {item.Status}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => StaffViewAsync(projectId, cancellationToken));
    }

    // ================================================================== Responsible technician

    public async Task<IReadOnlyList<InstallationTechnicianView>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
    {
        var list = new List<InstallationTechnicianView>();
        foreach (var p in await unitOfWork.ListForTechnicianAsync(technicianUserId, cancellationToken))
            if (await VisibleAssignmentAsync(technicianUserId, p.Id, cancellationToken) is { } assignment)
                list.Add(await TechnicianViewAsync(p.Id, assignment, cancellationToken));
        return list;
    }

    /// <summary>Only a technician with a current (PENDING/ACCEPTED) or COMPLETED assignment on the project can read it (others: 404).</summary>
    public async Task<OperationResult<InstallationTechnicianView>> GetForTechnicianAsync(Guid technicianUserId, Guid projectId, CancellationToken cancellationToken)
        => await VisibleAssignmentAsync(technicianUserId, projectId, cancellationToken) is { } assignment
            ? OperationResult<InstallationTechnicianView>.Ok(await TechnicianViewAsync(projectId, assignment, cancellationToken))
            : NotFound<InstallationTechnicianView>();

    /// <summary>PENDING assignment → ACCEPTED; PENDING_TECHNICIAN_ACCEPTANCE → SCHEDULED (a started project keeps its status). Repeating it is a no-op.</summary>
    public Task<OperationResult<InstallationTechnicianView>> AcceptAssignmentAsync(Guid technicianUserId, Guid projectId, int? expectedRevision, CancellationToken cancellationToken)
        => TechnicianActionAsync(technicianUserId, projectId, expectedRevision, requireAccepted: false, async (p, assignment, now, _) =>
        {
            if (assignment.Status == AssignmentStatuses.Accepted) return OperationResult<bool>.Fail(Noop, "already accepted");
            if (now >= assignment.EndsAt)
                return Fail(InstallationErrorCodes.InvalidState, "The assigned interval has passed. Ask the manager to reschedule.");
            var from = p.Status;
            assignment.Status = AssignmentStatuses.Accepted;
            assignment.RespondedAt = now;
            p.TechnicianAcceptedAt = now;
            if (p.Status == InstallationStatuses.PendingTechnicianAcceptance) p.Status = InstallationStatuses.Scheduled;
            Event(projectId, InstallationEventTypes.AssignmentAccepted, technicianUserId, from, p.Status, null, now);
            return await Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>
    /// PENDING assignment → DECLINED (reason required, visible to the Manager); PENDING_TECHNICIAN_ACCEPTANCE → PENDING_ASSIGNMENT.
    /// An accepted job cannot be declined (the Manager reassigns it).
    /// </summary>
    public async Task<OperationResult<InstallationTechnicianView>> DeclineAssignmentAsync(Guid technicianUserId, Guid projectId, string? reason, int? expectedRevision,
        CancellationToken cancellationToken)
    {
        var why = Trim(reason, 500);
        if (why is null) return OperationResult<InstallationTechnicianView>.Fail(InstallationErrorCodes.ReasonRequired, "Tell the manager why you decline.");
        Guid? declined = null;
        var result = await MutateTechnicianAsync(technicianUserId, projectId, expectedRevision, requireAccepted: false, (p, assignment, now, _) =>
        {
            if (assignment.Status != AssignmentStatuses.Pending)
                return Task.FromResult(Fail(InstallationErrorCodes.AssignmentNotPending, "Only an assignment waiting for your answer can be declined."));
            var from = p.Status;
            assignment.Status = AssignmentStatuses.Declined;
            assignment.RespondedAt = now;
            assignment.ResponseReason = why;
            assignment.ClosedAt = now;
            declined = assignment.Id;
            p.ResponsibleTechnicianUserId = null;
            p.TechnicianAcceptedAt = null;
            if (p.Status == InstallationStatuses.PendingTechnicianAcceptance) p.Status = InstallationStatuses.PendingAssignment;
            Event(projectId, InstallationEventTypes.AssignmentDeclined, technicianUserId, from, p.Status, why, now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
        if (!result.Succeeded) return result.As<InstallationTechnicianView>();
        var assignment = (await unitOfWork.GetAssignmentsAsync(projectId, false, cancellationToken)).Single(a => a.Id == declined);
        return OperationResult<InstallationTechnicianView>.Ok(await TechnicianViewAsync(projectId, assignment, cancellationToken));
    }

    /// <summary>
    /// SCHEDULED → IN_PROGRESS: accepted assignment, on/after the scheduled local start date and before the scheduled end
    /// (afterwards INSTALLATION_SCHEDULE_EXPIRED: the Manager must reschedule), required checklist satisfied.
    /// </summary>
    public Task<OperationResult<InstallationTechnicianView>> StartProjectAsync(Guid technicianUserId, Guid projectId, CancellationToken cancellationToken)
        => TechnicianActionAsync(technicianUserId, projectId, null, requireAccepted: true, async (p, _, now, token) =>
        {
            if (p.Status == InstallationStatuses.InProgress) return OperationResult<bool>.Fail(Noop, "already started");
            if (p.Status != InstallationStatuses.Scheduled) return Fail(InstallationErrorCodes.InvalidState, "Only scheduled projects can start.");
            if (calendar.LocalDateOf(now) < calendar.LocalDateOf(p.ScheduledStartsAt!.Value))
                return Fail(InstallationErrorCodes.TooEarlyToStart, "The installation can start on or after its scheduled start date.");
            if (now >= p.ScheduledEndsAt!.Value)
                return Fail(InstallationErrorCodes.ScheduleExpired, "The scheduled interval has ended. Ask the manager to reschedule before starting.");
            var blocking = BlockingChecklist(await unitOfWork.GetChecklistAsync(projectId, false, token));
            if (blocking.Count > 0) return OperationResult<bool>.Fail(InstallationErrorCodes.ChecklistIncomplete, "Required checklist items are not satisfied.", new { blocking });
            p.Status = InstallationStatuses.InProgress;
            p.StartedAt = now;
            Event(projectId, InstallationEventTypes.ProjectStarted, technicianUserId, InstallationStatuses.Scheduled, p.Status, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Links a verified INSTALLATION_EVIDENCE photo uploaded by the responsible technician to the current round (each file once).</summary>
    public async Task<OperationResult<InstallationTechnicianView>> AddEvidenceAsync(Guid technicianUserId, Guid projectId, Guid fileId, string? caption, CancellationToken cancellationToken)
    {
        if (await VisibleAssignmentAsync(technicianUserId, projectId, cancellationToken) is not { } visible || !AssignmentStatuses.IsCurrent(visible.Status))
            return NotFound<InstallationTechnicianView>();
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.InstallationEvidence, technicianUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<InstallationTechnicianView>();
        return await TechnicianActionAsync(technicianUserId, projectId, null, requireAccepted: true, async (p, _, now, token) =>
        {
            if (!InstallationStatuses.IsStarted(p.Status)) return Fail(InstallationErrorCodes.InvalidState, "Evidence is added while the installation is in progress or in rework.");
            if (await unitOfWork.IsEvidenceFileUsedAsync(fileId, token)) return Fail(FileErrorCodes.AlreadyLinked, "This photo is already used as evidence.");
            unitOfWork.Add(new InstallationEvidence
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Round = p.CurrentRound, FileId = fileId, Caption = Trim(caption, 300),
                AddedByUserId = technicianUserId, AddedAt = now,
            });
            Event(projectId, InstallationEventTypes.EvidenceAdded, technicianUserId, null, null, $"round {p.CurrentRound}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    public async Task<OperationResult<InstallationTechnicianView>> RegisterAssetsAsync(Guid technicianUserId, Guid projectId, IReadOnlyList<AssetInput> assets, CancellationToken cancellationToken)
    {
        if (assets.Count is < 1 or > 500)
            return OperationResult<InstallationTechnicianView>.Fail(CommonErrorCodes.ValidationFailed, "Register between 1 and 500 assets at a time.");
        foreach (var a in assets)
        {
            if (!AssetTypes.All.Contains(a.AssetType?.Trim().ToUpperInvariant() ?? "") || string.IsNullOrWhiteSpace(a.Manufacturer) || a.Manufacturer.Trim().Length > 100
                || string.IsNullOrWhiteSpace(a.Model) || a.Model.Trim().Length > 100
                || string.IsNullOrWhiteSpace(a.SerialNumber) || a.SerialNumber.Trim().Length > 100 || a.WarrantyMonths is < 0 or > 600)
                return OperationResult<InstallationTechnicianView>.Fail(CommonErrorCodes.ValidationFailed,
                    $"Each asset needs a type ({string.Join(", ", AssetTypes.All)}), manufacturer and model (≤100), serial number (≤100) and a warranty of 0–600 months.");
        }
        var keys = assets.Select(a => (a.Manufacturer.Trim().ToUpperInvariant(), a.SerialNumber.Trim().ToUpperInvariant())).ToList();
        if (keys.Distinct().Count() != keys.Count)
            return OperationResult<InstallationTechnicianView>.Fail(InstallationErrorCodes.DuplicateSerial, "The same serial number appears twice in the request.");

        return await TechnicianActionAsync(technicianUserId, projectId, null, requireAccepted: true, async (p, _, now, token) =>
        {
            if (!InstallationStatuses.IsStarted(p.Status))
                return Fail(InstallationErrorCodes.InvalidState, "Assets are registered while the installation is in progress or in rework.");
            foreach (var (manufacturer, serial) in keys)
                if (await unitOfWork.SerialExistsAsync(manufacturer, serial, token))
                    return OperationResult<bool>.Fail(InstallationErrorCodes.DuplicateSerial, $"Serial {serial} from {manufacturer} is already registered.");
            foreach (var a in assets)
                unitOfWork.Add(new InstalledAsset
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, AssetType = a.AssetType.Trim().ToUpperInvariant(), ProductId = a.ProductId,
                    Manufacturer = a.Manufacturer.Trim().ToUpperInvariant(), Model = a.Model.Trim(), SerialNumber = a.SerialNumber.Trim().ToUpperInvariant(),
                    PositionCode = Trim(a.PositionCode, 50), WarrantyMonths = a.WarrantyMonths, RegisteredByUserId = technicianUserId, RegisteredAt = now,
                });
            Event(projectId, InstallationEventTypes.AssetsRegistered, technicianUserId, null, null, $"{assets.Count} assets", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// IN_PROGRESS/REWORK_REQUIRED → PENDING_CUSTOMER_ACCEPTANCE: at least one evidence photo of the current round and (configurable)
    /// a registered PANEL and INVERTER. Opens acceptance round <c>AcceptanceRound + 1</c>.
    /// </summary>
    public async Task<OperationResult<InstallationTechnicianView>> ReportCompletionAsync(Guid technicianUserId, Guid projectId, string? completionReport, int? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(completionReport) || completionReport.Trim().Length > 8000)
            return OperationResult<InstallationTechnicianView>.Fail(CommonErrorCodes.ValidationFailed, "A completion report (≤ 8000 characters) is required.");
        return await TechnicianActionAsync(technicianUserId, projectId, expectedRevision, requireAccepted: true, async (p, _, now, token) =>
        {
            if (!InstallationStatuses.IsStarted(p.Status))
                return Fail(InstallationErrorCodes.InvalidState, "Completion is reported for a project in progress or in rework.");
            var round = p.CurrentRound;
            if (!(await unitOfWork.GetEvidenceAsync(projectId, token)).Any(e => e.Round == round))
                return Fail(InstallationErrorCodes.EvidenceRequired, $"Add at least one verified photo for round {round} before reporting completion.");
            var assets = await unitOfWork.GetAssetsAsync(projectId, token);
            if (options.RequirePanelAndInverterAssets && (!assets.Any(a => a.AssetType == AssetTypes.Panel) || !assets.Any(a => a.AssetType == AssetTypes.Inverter)))
                return Fail(InstallationErrorCodes.AssetsRequired, "Register the installed panels and inverter before reporting completion.");
            if ((await unitOfWork.GetAcceptancesAsync(projectId, false, token)).Any(a => a.Decision == AcceptanceDecisions.Pending))
                return Fail(InstallationErrorCodes.InvalidState, "A completion report is already waiting for the customer.");

            var from = p.Status;
            p.AcceptanceRound = round;
            unitOfWork.Add(new InstallationAcceptance
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Round = round, RequestedByUserId = technicianUserId, RequestedAt = now,
                CompletionReport = completionReport.Trim(), Decision = AcceptanceDecisions.Pending,
            });
            p.Status = InstallationStatuses.PendingCustomerAcceptance;
            p.AcceptanceRequestedAt = now;
            Event(projectId, InstallationEventTypes.CompletionReported, technicianUserId, from, p.Status, $"round {round}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== Customer

    public async Task<IReadOnlyList<InstallationCustomerView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<InstallationCustomerView>();
        foreach (var p in await unitOfWork.ListForCustomerAsync(customerUserId, cancellationToken)) list.Add(await CustomerViewAsync(p.Id, cancellationToken));
        return list;
    }

    public async Task<OperationResult<InstallationCustomerView>> GetForCustomerAsync(Guid customerUserId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        return project is null || project.CustomerUserId != customerUserId
            ? NotFound<InstallationCustomerView>()
            : OperationResult<InstallationCustomerView>.Ok(await CustomerViewAsync(projectId, cancellationToken));
    }

    /// <summary>
    /// PENDING_CUSTOMER_ACCEPTANCE → COMPLETED (handover; assignment COMPLETED) or → REWORK_REQUIRED (reason required; the next
    /// report needs new evidence). Decisions are immutable; repeating an acceptance of a completed project is a no-op.
    /// </summary>
    public async Task<OperationResult<InstallationCustomerView>> DecideAcceptanceAsync(Guid customerUserId, Guid projectId, bool accept, string? reason, CancellationToken cancellationToken)
    {
        if (!accept && string.IsNullOrWhiteSpace(reason))
            return OperationResult<InstallationCustomerView>.Fail(CommonErrorCodes.ValidationFailed, "Describe what needs to be fixed.");
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || project.CustomerUserId != customerUserId) return NotFound<InstallationCustomerView>();

        var result = await MutateAsync(projectId, null, null, async (p, now, token) =>
        {
            if (accept && p.Status == InstallationStatuses.Completed) return OperationResult<bool>.Fail(Noop, "already accepted");
            if (p.Status != InstallationStatuses.PendingCustomerAcceptance) return Fail(InstallationErrorCodes.InvalidState, "There is no installation waiting for your acceptance.");
            var pending = (await unitOfWork.GetAcceptancesAsync(projectId, true, token)).SingleOrDefault(a => a.Decision == AcceptanceDecisions.Pending);
            if (pending is null) return Fail(InstallationErrorCodes.InvalidState, "There is no pending completion report.");
            pending.DecidedByUserId = customerUserId;
            pending.DecidedAt = now;
            pending.Reason = Trim(reason, 2000);
            var from = p.Status;
            if (accept)
            {
                pending.Decision = AcceptanceDecisions.Accepted;
                p.Status = InstallationStatuses.Completed;
                p.CompletedAt = now;
                p.HandoverAt = now;
                if (await unitOfWork.GetCurrentAssignmentAsync(projectId, true, token) is { } current)
                {
                    current.Status = AssignmentStatuses.Completed;
                    current.ClosedAt = now;
                }
                Event(projectId, InstallationEventTypes.CustomerAccepted, customerUserId, from, p.Status, $"round {pending.Round}", now);
            }
            else
            {
                pending.Decision = AcceptanceDecisions.ReworkRequested;
                p.Status = InstallationStatuses.ReworkRequired;
                Event(projectId, InstallationEventTypes.ReworkRequested, customerUserId, from, p.Status, $"round {pending.Round}: {Trim(reason, 400)}", now);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => CustomerViewAsync(projectId, cancellationToken));
    }

    /// <summary>Handover of a COMPLETED project (customer owner or staff): assets with warranty end dates, evidence and acceptance history.</summary>
    public async Task<OperationResult<HandoverView>> GetHandoverAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || (!isStaff && project.CustomerUserId != userId)) return NotFound<HandoverView>();
        if (project.Status != InstallationStatuses.Completed || project.HandoverAt is not { } handover)
            return OperationResult<HandoverView>.Fail(InstallationErrorCodes.NotHandedOver, "The installation has not been handed over yet.");
        var start = calendar.LocalDateOf(handover);
        var assets = (await unitOfWork.GetAssetsAsync(projectId, cancellationToken))
            .Select(a => new HandoverAssetView(a.AssetType, a.ProductId, a.Manufacturer, a.Model, a.SerialNumber, a.PositionCode, a.WarrantyMonths,
                a.WarrantyMonths is { } months ? start.AddMonths(months) : null)).ToList();
        var technician = (await unitOfWork.GetAssignmentsAsync(projectId, false, cancellationToken)).Where(a => a.Status == AssignmentStatuses.Completed)
            .Select(a => (Guid?)a.TechnicianUserId).FirstOrDefault();
        var names = technician is { } t ? await unitOfWork.GetUserNamesAsync([t], cancellationToken) : new Dictionary<Guid, string>();
        return OperationResult<HandoverView>.Ok(new HandoverView(projectId, project.ContractId, Local(handover)!.Value,
            technician is { } id ? names.GetValueOrDefault(id) : null, assets,
            await EvidenceAsync(projectId, cancellationToken), await AcceptancesAsync(projectId, cancellationToken)));
    }

    // ================================================================== internals

    /// <param name="allowNoZone">Candidates and assignment accept a commune without an active zone (every technician is then cross-zone).</param>
    private async Task<OperationResult<SiteCoverage>> CoverageAsync(InstallationProject project, CancellationToken cancellationToken, bool allowNoZone = false)
    {
        var coverage = await scheduling.GetSiteCoverageAsync(project.PropertySiteId, cancellationToken);
        return CoverageFailure<SiteCoverage>(coverage, allowNoZone) ?? OperationResult<SiteCoverage>.Ok(coverage);
    }

    /// <summary>True when the technician has an active membership in the site's (active) zone.</summary>
    private async Task<bool> MemberOfAsync(Guid technicianUserId, SiteCoverage coverage, CancellationToken cancellationToken)
        => coverage.Zone is { } zone && (await scheduling.ListTechnicianZonesAsync(technicianUserId, cancellationToken)).Any(z => z.Id == zone.Id);

    /// <summary>Same codes and messages as survey booking: SITE_LOCATION_REQUIRED, NO_ZONE_COVERAGE, ZONE_NOT_SERVICEABLE (422); null when covered.</summary>
    private static OperationResult<T>? CoverageFailure<T>(SiteCoverage coverage, bool allowNoZone = false) => coverage.Status switch
    {
        CoverageStatuses.SiteLocationRequired => OperationResult<T>.Fail(InstallationErrorCodes.SiteLocationRequired, CoverageMessages.SiteLocationRequired),
        CoverageStatuses.NoZoneCoverage when allowNoZone => null,
        CoverageStatuses.NoZoneCoverage => OperationResult<T>.Fail(InstallationErrorCodes.NoZoneCoverage, CoverageMessages.NoZoneCoverage),
        CoverageStatuses.ZoneNotServiceable => OperationResult<T>.Fail(InstallationErrorCodes.ZoneNotServiceable, CoverageMessages.ZoneNotServiceable),
        _ => null,
    };

    /// <summary>The technician's assignment that lets them see the project: current (PENDING/ACCEPTED), else the COMPLETED one.</summary>
    private async Task<InstallationAssignment?> VisibleAssignmentAsync(Guid technicianUserId, Guid projectId, CancellationToken cancellationToken)
        => VisibleAssignmentOf(await unitOfWork.GetAssignmentsAsync(projectId, false, cancellationToken), technicianUserId);

    /// <summary>Shared with <see cref="InstallationEvidenceHandlers"/>: the same visibility rule for every technician read.</summary>
    internal static InstallationAssignment? VisibleAssignmentOf(IEnumerable<InstallationAssignment> assignments, Guid technicianUserId)
    {
        var mine = assignments.Where(a => a.TechnicianUserId == technicianUserId).ToList();
        return mine.FirstOrDefault(a => AssignmentStatuses.IsCurrent(a.Status)) ?? mine.Where(a => a.Status == AssignmentStatuses.Completed).MaxBy(a => a.AssignedAt);
    }

    /// <summary>A responsible-technician change; returns the technician view of the (still current) assignment.</summary>
    private async Task<OperationResult<InstallationTechnicianView>> TechnicianActionAsync(Guid technicianUserId, Guid projectId, int? expectedRevision, bool requireAccepted,
        Func<InstallationProject, InstallationAssignment, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        var result = await MutateTechnicianAsync(technicianUserId, projectId, expectedRevision, requireAccepted, change, cancellationToken);
        if (!result.Succeeded) return result.As<InstallationTechnicianView>();
        return await GetForTechnicianAsync(technicianUserId, projectId, cancellationToken);
    }

    /// <summary>
    /// The caller must be the technician of the project's current assignment (re-read inside the transaction, so a concurrent
    /// reassignment wins cleanly); otherwise 404 without revealing the project. Execution actions also need it ACCEPTED.
    /// </summary>
    private async Task<OperationResult<bool>> MutateTechnicianAsync(Guid technicianUserId, Guid projectId, int? expectedRevision, bool requireAccepted,
        Func<InstallationProject, InstallationAssignment, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        if (await VisibleAssignmentAsync(technicianUserId, projectId, cancellationToken) is not { } visible || !AssignmentStatuses.IsCurrent(visible.Status))
            return Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
        return await MutateAsync(projectId, expectedRevision, [technicianUserId], async (p, now, token) =>
        {
            var current = await unitOfWork.GetCurrentAssignmentAsync(projectId, true, token);
            if (current is null || current.TechnicianUserId != technicianUserId) return Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
            if (requireAccepted && current.Status != AssignmentStatuses.Accepted)
                return Fail(InstallationErrorCodes.AssignmentNotAccepted, "Accept the assignment first.");
            return await change(p, current, now, token);
        }, cancellationToken);
    }

    /// <summary>
    /// One transaction: optional technician locks, re-read the project, check the expected revision, bump the revision, apply the
    /// change, save, commit. A <see cref="Noop"/> outcome rolls back and reports success. Database conflicts map to stable codes.
    /// </summary>
    private async Task<OperationResult<bool>> MutateAsync(Guid projectId, int? expectedRevision, IEnumerable<Guid>? lockTechnicians,
        Func<InstallationProject, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                if (lockTechnicians is not null) await unitOfWork.LockTechniciansAsync(lockTechnicians, token);
                var project = await unitOfWork.FindProjectAsync(projectId, true, token);
                if (project is null) return (Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found."), false);
                if (expectedRevision is { } expected && project.Revision != expected)
                    return (OperationResult<bool>.Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.",
                        new { currentRevision = project.Revision }), false);
                var now = time.GetUtcNow();
                // The revision guard is part of the first UPDATE of the project row (concurrency token).
                project.UpdatedAt = now;
                project.Revision++;
                var result = await change(project, now, token);
                if (!result.Succeeded) return (result, false);
                await unitOfWork.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            return outcome.ErrorCode == Noop ? OperationResult<bool>.Ok(true) : outcome;
        }
        catch (SchedulingConflictException ex)
        {
            // Survey holds re-homed inside an assignment are saved through the scheduling unit of work.
            return ex.Kind == SchedulingConflictException.TechnicianOverlap
                ? Fail(EligibilityReasons.ScheduleConflict, IneligibleMessage(EligibilityReasons.ScheduleConflict))
                : Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
        }
        catch (PersistenceConflictException ex)
        {
            if (ex.Kind == PersistenceConflictException.ExclusionViolation)
                return Fail(EligibilityReasons.ScheduleConflict, IneligibleMessage(EligibilityReasons.ScheduleConflict));
            if (ex.Kind == PersistenceConflictException.UniqueViolation && ex.Constraint?.Contains("serial", StringComparison.OrdinalIgnoreCase) == true)
                return Fail(InstallationErrorCodes.DuplicateSerial, "A serial number is already registered.");
            if (ex.Kind == PersistenceConflictException.UniqueViolation && ex.Constraint?.Contains("evidence", StringComparison.OrdinalIgnoreCase) == true)
                return Fail(FileErrorCodes.AlreadyLinked, "This photo is already used as evidence.");
            return Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
        }
    }

    private static async Task<OperationResult<TView>> ThenAsync<TView>(OperationResult<bool> result, Func<Task<TView>> view)
        => result.Succeeded ? OperationResult<TView>.Ok(await view()) : result.As<TView>();

    private static bool SameZone(SiteCoverage a, SiteCoverage b) => a.Zone?.Id == b.Zone?.Id;

    private static IReadOnlySet<Guid>? Ignore(InstallationAssignment? current) => current is null ? null : new HashSet<Guid> { current.Id };

    private static List<string> BlockingChecklist(IReadOnlyList<InstallationChecklistItem> items)
        => items.Where(i => i.IsRequired && i.Status == ChecklistStatuses.Pending).Select(i => i.Code).ToList();

    private DateTimeOffset? Local(DateTimeOffset? instant) => instant is { } value ? TimeZoneInfo.ConvertTime(value, calendar.TimeZone) : null;

    private async Task<IReadOnlyList<EvidenceView>> EvidenceAsync(Guid projectId, CancellationToken cancellationToken)
        => (await unitOfWork.GetEvidenceAsync(projectId, cancellationToken)).OrderBy(e => e.Round).ThenBy(e => e.AddedAt)
            .Select(e => new EvidenceView(e.Id, e.FileId, e.Round, e.Caption, Local(e.AddedAt)!.Value)).ToList();

    /// <summary>Acceptance rounds; <see cref="AcceptanceView.DecidedBy"/> tells a customer decision from a Manager's return to rework (no staff identity).</summary>
    private async Task<IReadOnlyList<AcceptanceView>> AcceptancesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var customer = (await unitOfWork.FindProjectAsync(projectId, false, cancellationToken))?.CustomerUserId;
        return (await unitOfWork.GetAcceptancesAsync(projectId, false, cancellationToken)).OrderBy(a => a.Round)
            .Select(a => new AcceptanceView(a.Round, Local(a.RequestedAt)!.Value, a.CompletionReport, a.Decision, Local(a.DecidedAt), a.Reason,
                a.DecidedByUserId is not { } by ? null : by == customer ? AcceptanceDeciders.Customer : AcceptanceDeciders.Staff)).ToList();
    }

    private async Task<IReadOnlyList<AssetView>> AssetsAsync(Guid projectId, CancellationToken cancellationToken)
        => (await unitOfWork.GetAssetsAsync(projectId, cancellationToken))
            .Select(a => new AssetView(a.Id, a.AssetType, a.ProductId, a.Manufacturer, a.Model, a.SerialNumber, a.PositionCode, a.WarrantyMonths)).ToList();

    private async Task<InstallationStaffView> StaffViewAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var p = (await unitOfWork.FindProjectAsync(projectId, false, cancellationToken))!;
        var assignments = (await unitOfWork.GetAssignmentsAsync(projectId, false, cancellationToken)).OrderBy(a => a.AssignedAt).ToList();
        var names = await unitOfWork.GetUserNamesAsync(assignments.Select(a => a.TechnicianUserId).Distinct().ToList(), cancellationToken);
        var current = assignments.FirstOrDefault(a => AssignmentStatuses.IsCurrent(a.Status));
        var zone = p.OperationalZoneId is { } zoneId ? await unitOfWork.GetZoneSummaryAsync(zoneId, cancellationToken) : null;
        return new InstallationStaffView(p.Id, p.ContractId, p.SurveyRequestId, p.CustomerUserId, p.Status, zone,
            current is null ? null : new ResponsibleTechnicianView(current.TechnicianUserId, names.GetValueOrDefault(current.TechnicianUserId, ""), current.Status, Local(current.RespondedAt)),
            Local(p.ScheduledStartsAt), Local(p.ScheduledEndsAt), p.PlannedStartDate, p.PlannedEndDate, Local(p.StartedAt), Local(p.AcceptanceRequestedAt),
            Local(p.CompletedAt), Local(p.HandoverAt), Local(p.CancelledAt), p.CancellationReason, p.CurrentRound, p.Revision,
            await unitOfWork.GetSiteAsync(p.PropertySiteId, p.CustomerUserId, cancellationToken),
            assignments.Select(a => new AssignmentView(a.Id, a.TechnicianUserId, names.GetValueOrDefault(a.TechnicianUserId, ""), Local(a.StartsAt)!.Value, Local(a.EndsAt)!.Value,
                a.Status, a.AssignedByUserId, Local(a.AssignedAt)!.Value, a.AssignmentReason, Local(a.RespondedAt), a.ResponseReason, Local(a.ClosedAt))).ToList(),
            await EvidenceAsync(projectId, cancellationToken), await AssetsAsync(projectId, cancellationToken), await AcceptancesAsync(projectId, cancellationToken),
            (await unitOfWork.GetChecklistAsync(projectId, false, cancellationToken))
                .Select(i => new ChecklistItemView(i.Id, i.Code, i.Title, i.IsRequired, i.Status, i.EvidenceFileId, i.Note, Local(i.VerifiedAt))).ToList(),
            (await unitOfWork.GetEventsAsync(projectId, cancellationToken))
                .Select(e => new InstallationEventView(e.EventType, e.ActorUserId, e.FromStatus, e.ToStatus, e.Detail, Local(e.CreatedAt)!.Value)).ToList());
    }

    private async Task<InstallationTechnicianView> TechnicianViewAsync(Guid projectId, InstallationAssignment assignment, CancellationToken cancellationToken)
    {
        var p = (await unitOfWork.FindProjectAsync(projectId, false, cancellationToken))!;
        var involved = assignment.Status is AssignmentStatuses.Pending or AssignmentStatuses.Accepted or AssignmentStatuses.Completed;
        string? blocked = null;
        if (assignment.Status != AssignmentStatuses.Accepted) blocked = InstallationErrorCodes.AssignmentNotAccepted;
        else if (p.Status != InstallationStatuses.Scheduled) blocked = InstallationErrorCodes.InvalidState;
        else if (calendar.LocalDateOf(time.GetUtcNow()) < calendar.LocalDateOf(p.ScheduledStartsAt!.Value)) blocked = InstallationErrorCodes.TooEarlyToStart;
        else if (time.GetUtcNow() >= p.ScheduledEndsAt!.Value) blocked = InstallationErrorCodes.ScheduleExpired;
        else if (BlockingChecklist(await unitOfWork.GetChecklistAsync(projectId, false, cancellationToken)).Count > 0) blocked = InstallationErrorCodes.ChecklistIncomplete;
        var zone = p.OperationalZoneId is { } zoneId ? await unitOfWork.GetZoneSummaryAsync(zoneId, cancellationToken) : null;
        return new InstallationTechnicianView(p.Id, p.Status, assignment.Status, assignment.Id, Local(assignment.StartsAt)!.Value, Local(assignment.EndsAt)!.Value,
            zone?.Name, involved ? await unitOfWork.GetSiteAsync(p.PropertySiteId, p.CustomerUserId, cancellationToken) : null,
            Local(p.StartedAt), Local(p.AcceptanceRequestedAt), Local(p.CompletedAt), p.CurrentRound, p.Revision, blocked is null, blocked,
            involved ? await EvidenceAsync(projectId, cancellationToken) : [],
            involved ? await AssetsAsync(projectId, cancellationToken) : [],
            involved ? await AcceptancesAsync(projectId, cancellationToken) : []);
    }

    private async Task<InstallationCustomerView> CustomerViewAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var p = (await unitOfWork.FindProjectAsync(projectId, false, cancellationToken))!;
        string? technician = null;
        if (p.TechnicianAcceptedAt is not null && p.ResponsibleTechnicianUserId is { } t)
            technician = (await unitOfWork.GetUserNamesAsync([t], cancellationToken)).GetValueOrDefault(t);
        var scheduled = p.Status is not (InstallationStatuses.PendingAssignment or InstallationStatuses.Cancelled);
        return new InstallationCustomerView(p.Id, p.ContractId, p.Status, scheduled ? Local(p.ScheduledStartsAt) : null, scheduled ? Local(p.ScheduledEndsAt) : null,
            technician, Local(p.StartedAt), Local(p.AcceptanceRequestedAt), Local(p.CompletedAt), Local(p.HandoverAt), Local(p.CancelledAt), p.CurrentRound, p.Revision,
            await EvidenceAsync(projectId, cancellationToken), await AssetsAsync(projectId, cancellationToken), await AcceptancesAsync(projectId, cancellationToken));
    }

    private void Event(Guid projectId, string type, Guid actor, string? from, string? to, string? detail, DateTimeOffset now)
        => unitOfWork.Add(new InstallationEvent
        {
            Id = Guid.NewGuid(), ProjectId = projectId, EventType = type, ActorUserId = actor, FromStatus = from == to ? null : from, ToStatus = from == to ? null : to,
            Detail = Trim(detail, 500), CreatedAt = now,
        });

    private static string IneligibleMessage(string reason) => reason switch
    {
        EligibilityReasons.OutsideZone => "The technician is not a member of the site's operational zone.",
        EligibilityReasons.OutsideWorkingHours => "The interval is outside the technician's working days or hours.",
        EligibilityReasons.OnLeave => "The technician is on leave during the interval.",
        EligibilityReasons.ScheduleConflict => "The technician has another survey appointment or installation within the buffer of this interval.",
        EligibilityReasons.SchedulingDisabled => "Scheduling is disabled for the technician.",
        _ => "The technician is not an active technician.",
    };

    private static OperationResult<T> Ineligible<T>(Guid technicianUserId, string name, string reason)
        => OperationResult<T>.Fail(reason, IneligibleMessage(reason), new { technicianUserId, technicianName = name });

    private static OperationResult<bool> Fail(string code, string message) => OperationResult<bool>.Fail(code, message);
    private static OperationResult<T> NotFound<T>() => OperationResult<T>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
