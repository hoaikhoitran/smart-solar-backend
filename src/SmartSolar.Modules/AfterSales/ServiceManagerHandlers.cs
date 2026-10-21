using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

/// <summary>
/// Manager scheduling input. <see cref="CrossZoneAuthorized"/> + <see cref="Reason"/> are required for a technician outside the site's
/// zone; <see cref="Reason"/> is also required to move an existing visit. <see cref="CompanyCaused"/> marks a move that SmartSolar caused
/// (a valid maintenance booking may then be completed during the extension window after the term ends).
/// </summary>
public sealed record ScheduleVisitInput(Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string? Reason, int ExpectedRevision,
    bool CrossZoneAuthorized = false, bool CompanyCaused = false);

public sealed record CoverageDecision(Guid ItemId, string Coverage, string? Reason);

public sealed record AssetCorrection(string? Manufacturer, string? Model, string? SerialNumber, string? PositionCode, string? Specifications, string Reason);

/// <summary>
/// Manager / Admin side of after-sales requests: scheduling technician visits (shared overlap rules and locks), warranty coverage per
/// diagnosed item, disaster-claim decisions (one reconsideration), duplicates, disputed completions, cancellation and refund
/// decisions, and equipment-data corrections with an audit trail.
/// </summary>
public sealed class ServiceManagerHandlers(
    IAfterSalesUnitOfWork data,
    ISchedulingUnitOfWork scheduling,
    InstallationSchedulePlanner planner,
    ISchedulingPolicyProvider policies,
    AfterSalesViews views,
    TimeProvider time)
{
    // ================================================================== queries

    public async Task<IReadOnlyList<ServiceRequestSummary>> ListAsync(string? status, string? kind, CancellationToken cancellationToken)
        => (await data.ListRequestsAsync(new RequestFilter(Status: status, Kind: kind), cancellationToken)).Select(views.Summary).ToList();

    public async Task<OperationResult<ServiceRequestView>> GetAsync(Guid requestId, CancellationToken cancellationToken)
        => await data.FindRequestAsync(requestId, false, cancellationToken) is { } request
            ? OperationResult<ServiceRequestView>.Ok(await views.RequestAsync(request, forStaff: true, cancellationToken))
            : NotFound();

    // ================================================================== visits

    /// <summary>
    /// Assigns a technician for the next visit the request needs: inspection (new fault), repair (authorized work), maintenance, or
    /// rework (Manager-confirmed complaint). Moving an open visit needs a reason. The technician is checked with the same rules as
    /// installation work (account, role, scheduling flag, working hours, leave, buffer, other commitments); pending survey holds in
    /// the interval make them unavailable. The interval is written to installation_assignment, whose exclusion constraint forbids
    /// overlaps with any other current commitment of that technician.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> ScheduleVisitAsync(Guid managerUserId, Guid requestId, ScheduleVisitInput input, CancellationToken cancellationToken)
    {
        var request = await data.FindRequestAsync(requestId, false, cancellationToken);
        if (request is null) return NotFound();
        var visits = await data.ListVisitsAsync(requestId, false, cancellationToken);
        var openVisit = visits.FirstOrDefault(v => VisitStatuses.IsOpen(v.Status));
        if (openVisit is not null && VisitStatuses.IsWaitingOnSite(openVisit))
            return Fail(AfterSalesErrorCodes.InvalidState, "The technician is waiting on site to repair during this visit; they continue or finish it first.");
        if (openVisit?.Status == VisitStatuses.InProgress) return Fail(AfterSalesErrorCodes.InvalidState, "The technician already started this visit.");
        var purpose = openVisit?.Purpose ?? PurposeFor(request);
        if (purpose is null) return Fail(AfterSalesErrorCodes.InvalidState, $"A {request.Status} request does not need a visit now.");
        var reason = Text.Trim(input.Reason, 500);
        if (openVisit is not null && reason is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "A reason is required to move a scheduled visit.");
        if (input.CompanyCaused && reason is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "Record why SmartSolar has to reschedule.");

        var now = time.GetUtcNow();
        var policy = await policies.GetAsync(cancellationToken);
        var startsAt = input.StartsAt.ToUniversalTime();
        var defaultMinutes = purpose == VisitPurposes.Inspection ? policy.Get(AfterSalesSettingKeys.InspectionDurationMinutes) : 120;
        var endsAt = input.EndsAt?.ToUniversalTime() ?? startsAt.AddMinutes(defaultMinutes);
        if (startsAt <= now || endsAt <= startsAt) return Fail(AfterSalesErrorCodes.ValidationFailed, "The visit must start in the future and end after it starts.");

        // A maintenance visit stays inside its term (or the extension window when SmartSolar moved a valid booking).
        DateTimeOffset? extendedUntil = null;
        if (request.Kind == ServiceRequestKinds.Maintenance)
        {
            var entitlement = (await data.FindEntitlementAsync(request.MaintenanceEntitlementId!.Value, false, cancellationToken))!;
            var term = (await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken))!;
            var latest = entitlement.ExtendedUntil ?? term.EndsAt!.Value;
            if (input.CompanyCaused) latest = term.EndsAt!.Value.AddDays(policy.Get(AfterSalesSettingKeys.DelayedMaintenanceExtensionDays));
            if (startsAt < entitlement.PeriodStartsAt || endsAt > latest)
                return Fail(AfterSalesErrorCodes.OutsidePeriod, $"The visit must be between {entitlement.PeriodStartsAt:O} and {latest:O}.");
            if (input.CompanyCaused && endsAt > term.EndsAt) extendedUntil = latest;
        }

        // Zone membership: a technician of another zone needs an explicit cross-zone authorization with a reason.
        var site = await scheduling.GetSiteCoverageAsync(request.PropertySiteId, cancellationToken);
        var member = site.Zone is { } zone && (await scheduling.ListTechnicianZonesAsync(input.TechnicianUserId, cancellationToken)).Any(z => z.Id == zone.Id);
        if (!member && (!input.CrossZoneAuthorized || reason is null))
            return Fail(AfterSalesErrorCodes.CrossZoneAuthorizationRequired,
                "The technician is not a member of the site's operational zone: set crossZoneAuthorized = true and give a reason.");
        var current = openVisit is null ? null : await data.FindCurrentAssignmentAsync(openVisit.Id, false, cancellationToken);
        var ignore = current is null ? null : new HashSet<Guid> { current.Id };
        if (await TechnicianProblemAsync(input.TechnicianUserId, startsAt, endsAt, member ? site.ZoneIds : null, ignore, cancellationToken) is { } problem)
            return Fail(AfterSalesErrorCodes.TechnicianConflict, problem.Message, new { reason = problem.Code });

        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var locks = new List<Guid> { input.TechnicianUserId };
                if (current is not null) locks.Add(current.TechnicianUserId);
                await data.LockTechniciansAsync(locks, token);
                // Re-check under the technician lock (survey bookings and installation assignments take the same lock).
                if (await TechnicianProblemAsync(input.TechnicianUserId, startsAt, endsAt, member ? site.ZoneIds : null, ignore, token) is { } again)
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.TechnicianConflict, again.Message, new { reason = again.Code }), false);
                var tracked = (await data.FindRequestAsync(requestId, true, token))!;
                if (tracked.Revision != input.ExpectedRevision) return (Stale<bool>(tracked.Revision), false);

                if (openVisit is not null)
                {
                    var old = (await data.FindVisitAsync(openVisit.Id, true, token))!;
                    old.Status = VisitStatuses.Cancelled;
                    old.UpdatedAt = now;
                    if (await data.FindCurrentAssignmentAsync(old.Id, true, token) is { } oldAssignment)
                    {
                        oldAssignment.Status = AssignmentStatuses.Replaced;
                        oldAssignment.ClosedAt = now;
                    }
                    await data.SaveChangesAsync(token);   // frees the old interval before the new one is written
                }

                var visit = new ServiceVisit
                {
                    Id = Guid.NewGuid(), RequestId = requestId, Purpose = purpose, Status = VisitStatuses.Scheduled, TechnicianUserId = input.TechnicianUserId,
                    StartsAt = startsAt, EndsAt = endsAt, CompanyRescheduleReason = input.CompanyCaused ? reason : null,
                    // The customer asked for a time; any other time must be confirmed by them before the visit.
                    CustomerConfirmationRequired = tracked.PreferredStartsAt is { } preferred && preferred != startsAt,
                    AssignedByUserId = managerUserId, CreatedAt = now, UpdatedAt = now,
                };
                data.Add(visit);
                await data.SaveChangesAsync(token);
                data.Add(new InstallationAssignment
                {
                    Id = Guid.NewGuid(), ProjectId = tracked.ProjectId, TechnicianUserId = input.TechnicianUserId, StartsAt = startsAt, EndsAt = endsAt,
                    Status = AssignmentStatuses.Pending, AssignedByUserId = managerUserId, AssignedAt = now, AssignmentReason = reason, ServiceVisitId = visit.Id,
                });
                if (extendedUntil is not null)
                {
                    var entitlement = (await data.FindEntitlementAsync(tracked.MaintenanceEntitlementId!.Value, true, token))!;
                    entitlement.ExtendedUntil = extendedUntil;
                    entitlement.UpdatedAt = now;
                    entitlement.Revision++;
                }
                var detail = $"{purpose} visit, technician {input.TechnicianUserId}, {startsAt:O} – {endsAt:O}";
                if (!member) detail += $"; cross-zone: {reason}";
                if (input.CompanyCaused) detail += $"; rescheduled by SmartSolar: {reason}";
                else if (openVisit is not null) detail += $"; moved: {reason}";
                AfterSalesHistory.Record(data, EventSubjects.Request, requestId, openVisit is null ? "VISIT_SCHEDULED" : "VISIT_RESCHEDULED", managerUserId, detail, now);
                AfterSalesWorkflow.Move(data, tracked, ServiceRequestStatuses.Scheduled, managerUserId, null, now);
                AfterSalesHistory.Notify(data, input.TechnicianUserId, "VISIT_ASSIGNED", $"New {purpose} visit {tracked.RequestNumber} on {startsAt:O}.", now, requestId);
                AfterSalesHistory.Notify(data, tracked.CustomerUserId, "VISIT_SCHEDULED",
                    visit.CustomerConfirmationRequired
                        ? $"We propose {startsAt:O} for {tracked.RequestNumber}; please confirm the appointment."
                        : $"Your {purpose.ToLowerInvariant()} visit for {tracked.RequestNumber} is planned for {startsAt:O}.", now, requestId);
                await data.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            return result.Succeeded ? await ViewAsync(requestId, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.ExclusionViolation)
        {
            return Fail(AfterSalesErrorCodes.TechnicianConflict, "The technician already has another commitment in this interval.");
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.");
        }
    }

    /// <summary>The visit purpose a request needs in its current status (null: no visit needed now).</summary>
    private static string? PurposeFor(ServiceRequest request) => (request.Kind, request.Status) switch
    {
        (ServiceRequestKinds.Fault, ServiceRequestStatuses.Submitted) => VisitPurposes.Inspection,
        (ServiceRequestKinds.Fault, ServiceRequestStatuses.ReadyForWork) => VisitPurposes.Repair,
        (ServiceRequestKinds.Maintenance, ServiceRequestStatuses.Submitted) => VisitPurposes.Maintenance,
        (ServiceRequestKinds.OneTimeMaintenance, ServiceRequestStatuses.ReadyForWork) => VisitPurposes.Maintenance,
        (_, ServiceRequestStatuses.ReworkRequired) => VisitPurposes.Rework,
        _ => null,
    };

    private sealed record TechnicianProblem(string Code, string Message);

    private async Task<TechnicianProblem?> TechnicianProblemAsync(Guid technicianUserId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        IReadOnlyCollection<Guid>? zoneIds, IReadOnlySet<Guid>? ignore, CancellationToken token)
    {
        var (_, reason, plan) = await planner.CheckAsync(technicianUserId, startsAt, endsAt, zoneIds, ignore, token);
        if (reason is not null) return new TechnicianProblem(reason, $"The technician cannot take this interval ({reason}).");
        // Survey capacity holds are not moved automatically for after-sales visits: pick another time or technician.
        if (plan is { } p && p.HoldIds.Count > 0)
            return new TechnicianProblem("SURVEY_HOLD_CONFLICT", "The technician has a pending survey appointment in this interval.");
        return null;
    }

    // ================================================================== coverage and claims

    /// <summary>
    /// Sets the coverage of the diagnosed items. Warranty priority: installation warranty or a previous paid repair's warranty first
    /// (free), then disaster protection (only after the claim was approved), otherwise chargeable. Free items are authorized at once;
    /// the others wait for a Sales quotation.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> DecideCoverageAsync(Guid managerUserId, Guid requestId, IReadOnlyList<CoverageDecision> decisions,
        CancellationToken cancellationToken)
    {
        if (decisions.Count == 0) return Fail(AfterSalesErrorCodes.ValidationFailed, "Give a decision for at least one item.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            if (ServiceRequestStatuses.IsFinal(request.Status)) return Bad("The request is closed.");
            var items = await data.ListItemsAsync(requestId, true, token);
            var claim = await data.FindClaimForRequestAsync(requestId, false, token);
            foreach (var decision in decisions)
            {
                var item = items.FirstOrDefault(i => i.Id == decision.ItemId);
                if (item is null) return OperationResult<bool>.Fail(AfterSalesErrorCodes.ItemNotFound, "Item was not found.");
                if (item.Status != ItemStatuses.Open) return Bad("Only items still open can be decided.");
                var problem = decision.Coverage switch
                {
                    ItemCoverages.InstallationWarranty when request.InstallationWarrantyEndsAt is null
                        => "No installation warranty duration is recorded for this contract (signed before warranty options existed); decide the item as chargeable or follow the legacy-contract policy.",
                    ItemCoverages.InstallationWarranty when !request.WithinInstallationWarranty => "The installation warranty had ended when the fault was reported.",
                    ItemCoverages.RepairWarranty when !request.WithinRepairWarranty => "No paid-repair warranty covered this installation when the fault was reported.",
                    ItemCoverages.DisasterProtection when claim?.Status != ClaimStatuses.Approved => "Approve the disaster-protection claim first.",
                    ItemCoverages.Chargeable or ItemCoverages.InstallationWarranty or ItemCoverages.RepairWarranty or ItemCoverages.DisasterProtection => null,
                    _ => "Unknown coverage.",
                };
                if (problem is not null) return Bad(problem);
                item.Coverage = decision.Coverage;
                item.CoverageReason = Text.Trim(decision.Reason, 1000);
                if (!ItemCoverages.NeedsQuotation(item.Coverage)) item.Status = ItemStatuses.Authorized;
                item.UpdatedAt = now;
                AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "COVERAGE_DECIDED", managerUserId, $"item {item.Id}: {item.Coverage}; {item.CoverageReason}", now);
            }
            // Work already on site (a supplementary item found during repair) does not change the request status until the report.
            if (request.Status is not (ServiceRequestStatuses.InProgress or ServiceRequestStatuses.Scheduled))
                AfterSalesWorkflow.Move(data, request, AfterSalesWorkflow.NextStatus(items), managerUserId, "coverage decided", now);
            if (items.Any(i => ItemCoverages.NeedsQuotation(i.Coverage) && i.Status == ItemStatuses.Open))
                await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.RepairQuotation, request.CustomerUserId, requestId, null, "Prepare the repair quotation.", now, token);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "COVERAGE_DECIDED", $"The diagnosis of {request.RequestNumber} was reviewed.", now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Approves or rejects a disaster-protection claim on the technician's evidence. A rejection needs a reason; the customer may then ask
    /// once for reconsideration, which is decided here again (both decisions stay in the history).
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> DecideClaimAsync(Guid managerUserId, Guid requestId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 1000);
        if (!approve && why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "A rejection reason is required.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            var claim = await data.FindClaimForRequestAsync(requestId, true, token);
            if (claim is null) return OperationResult<bool>.Fail(AfterSalesErrorCodes.RequestNotFound, "This request has no disaster-protection claim.");
            if (claim.Status is not (ClaimStatuses.Submitted or ClaimStatuses.ReconsiderationRequested))
                return Bad($"A {claim.Status} claim cannot be decided.");
            if (claim.Status == ClaimStatuses.ReconsiderationRequested) claim.FirstDecisionReason ??= claim.DecisionReason;
            claim.Status = approve ? ClaimStatuses.Approved : ClaimStatuses.Rejected;
            claim.DecisionReason = why;
            claim.DecidedByUserId = managerUserId;
            claim.DecidedAt = now;
            claim.UpdatedAt = now;
            claim.Revision++;
            AfterSalesHistory.Record(data, EventSubjects.Claim, claim.Id, approve ? "CLAIM_APPROVED" : "CLAIM_REJECTED", managerUserId, why, now);
            AfterSalesHistory.Record(data, EventSubjects.Request, requestId, approve ? "CLAIM_APPROVED" : "CLAIM_REJECTED", managerUserId, why, now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "CLAIM_DECIDED",
                approve ? $"Your disaster-protection claim for {request.RequestNumber} was approved." : $"Your disaster-protection claim for {request.RequestNumber} was rejected: {why}",
                now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== duplicates, disputes, cancellations

    /// <summary>Closes a duplicate request without sending another technician; the history and the link to the original are kept.</summary>
    public async Task<OperationResult<ServiceRequestView>> MarkDuplicateAsync(Guid managerUserId, Guid requestId, Guid originalRequestId, string? reason,
        CancellationToken cancellationToken)
    {
        var original = await data.FindRequestAsync(originalRequestId, false, cancellationToken);
        if (original is null || originalRequestId == requestId) return Fail(AfterSalesErrorCodes.RequestNotFound, "The original request was not found.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            if (ServiceRequestStatuses.IsFinal(request.Status)) return Bad("The request is already closed.");
            if (request.ProjectId != original.ProjectId) return Bad("A duplicate must belong to the same installation.");
            if ((await data.ListQuotationsAsync(requestId, false, token)).Any(q => q.Status == QuotationStatuses.Paid))
                return Bad("A request with a paid quotation cannot be closed as a duplicate.");
            await CloseEverythingAsync(request, managerUserId, "duplicate", now, token);
            request.DuplicateOfRequestId = originalRequestId;
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.ClosedDuplicate, managerUserId, $"duplicate of {original.RequestNumber}: {Text.Trim(reason, 500)}", now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "REQUEST_DUPLICATE",
                $"{request.RequestNumber} was merged into {original.RequestNumber} (same problem).", now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// The customer rejected the completed work. A valid complaint → REWORK_REQUIRED (schedule a rework visit; no extra maintenance visit is
    /// consumed). Otherwise the request goes back to the customer for acceptance — silence is never acceptance.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> ReviewDisputeAsync(Guid managerUserId, Guid requestId, bool requireRework, string? note,
        CancellationToken cancellationToken)
    {
        var why = Text.Trim(note, 1000);
        if (why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "Explain the decision to the customer.");
        return await MutateAsync(requestId, null, (request, now, _) =>
        {
            if (request.Status != ServiceRequestStatuses.Disputed) return Task.FromResult(Bad("Only a disputed completion can be reviewed."));
            AfterSalesWorkflow.Move(data, request, requireRework ? ServiceRequestStatuses.ReworkRequired : ServiceRequestStatuses.PendingCustomerAcceptance,
                managerUserId, why, now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "DISPUTE_REVIEWED",
                requireRework ? $"We will redo the work of {request.RequestNumber}." : $"Please review {request.RequestNumber} again: {why}", now, requestId);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>
    /// Decides a customer's cancellation request for paid work that has not started. Approval cancels the request and records an
    /// APPROVED refund of what was paid minus documented non-recoverable costs (an Admin executes and records it manually).
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> DecideCancellationAsync(Guid managerUserId, Guid requestId, bool approve, decimal nonRecoverableAmount,
        string? note, CancellationToken cancellationToken)
    {
        if (nonRecoverableAmount < 0 || decimal.Truncate(nonRecoverableAmount) != nonRecoverableAmount)
            return Fail(AfterSalesErrorCodes.ValidationFailed, "The non-recoverable amount must be a whole VND amount ≥ 0.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            if (request.CancellationRequestedAt is null) return Bad("The customer did not request a cancellation.");
            if (!approve)
            {
                AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "CANCELLATION_REJECTED", managerUserId, Text.Trim(note, 1000), now);
                request.CancellationRequestedAt = null;
                request.UpdatedAt = now;
                request.Revision++;
                AfterSalesHistory.Notify(data, request.CustomerUserId, "CANCELLATION_REJECTED", $"The cancellation of {request.RequestNumber} was not approved.", now, requestId);
                return OperationResult<bool>.Ok(true);
            }
            if ((await data.ListVisitsAsync(requestId, false, token)).Any(v => v.Status is VisitStatuses.InProgress))
                return Bad("Work has started: use the exceptional stop instead.");
            await RefundPaidQuotationsAsync(request, managerUserId, nonRecoverableAmount, "Cancelled before work started: " + request.CancellationReason, now, token);
            await CloseEverythingAsync(request, managerUserId, "cancelled", now, token);
            request.CancelledAt = now;
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Cancelled, managerUserId, Text.Trim(note, 500), now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "CANCELLATION_APPROVED", $"{request.RequestNumber} was cancelled.", now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Exceptional stop of a request after work started (the customer cannot cancel it online). A refund is recorded only when the
    /// Manager decides one is owed (<paramref name="refundAmount"/> &gt; 0); free warranty work never costs a cancellation fee.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> StopWorkAsync(Guid managerUserId, Guid requestId, string? reason, decimal refundAmount,
        CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 1000);
        if (why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "A reason is required.");
        if (refundAmount < 0 || decimal.Truncate(refundAmount) != refundAmount) return Fail(AfterSalesErrorCodes.ValidationFailed, "The refund must be a whole VND amount ≥ 0.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            if (ServiceRequestStatuses.IsFinal(request.Status)) return Bad("The request is already closed.");
            var paid = (await data.ListQuotationsAsync(requestId, false, token)).Where(q => q.Status == QuotationStatuses.Paid).ToList();
            if (refundAmount > paid.Sum(q => q.CustomerAmount)) return Bad("The refund cannot exceed what the customer paid.");
            if (refundAmount > 0) AddRefund(request, paid.FirstOrDefault(), paid.Sum(q => q.CustomerAmount), refundAmount, "Work stopped: " + why, managerUserId, now);
            await CloseEverythingAsync(request, managerUserId, "work stopped", now, token);
            request.CancelledAt = now;
            request.CancellationReason ??= why;
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Cancelled, managerUserId, "work stopped: " + why, now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "WORK_STOPPED", $"{request.RequestNumber} was stopped: {why}", now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// One-time maintenance that SmartSolar cannot provide (no suitable technician; the customer rejected the alternatives): the request is
    /// cancelled and a 100 % refund of the service payment is approved (executed manually by an Admin).
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> ConfirmCannotFulfillAsync(Guid managerUserId, Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 1000);
        if (why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "A reason is required.");
        return await MutateAsync(requestId, null, async (request, now, token) =>
        {
            if (request.Kind != ServiceRequestKinds.OneTimeMaintenance || ServiceRequestStatuses.IsFinal(request.Status))
                return Bad("Only an open one-time maintenance request can be closed this way.");
            if ((await data.ListVisitsAsync(requestId, false, token)).Any(v => v.Status is VisitStatuses.InProgress or VisitStatuses.Reported))
                return Bad("The maintenance was already performed.");
            await RefundPaidQuotationsAsync(request, managerUserId, 0, "SmartSolar could not provide the service: " + why, now, token);
            await CloseEverythingAsync(request, managerUserId, "not fulfilled", now, token);
            request.CancelledAt = now;
            request.CancellationReason ??= why;
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Cancelled, managerUserId, "SmartSolar could not fulfil: " + why, now);
            AfterSalesHistory.Notify(data, request.CustomerUserId, "SERVICE_NOT_FULFILLED", $"We are sorry: {request.RequestNumber} could not be scheduled. A full refund is approved.", now, requestId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== equipment corrections

    /// <summary>Corrects factual equipment data after acceptance (reason, old and new values in the audit trail). The customer does not accept again.</summary>
    public async Task<OperationResult<AssetView>> CorrectAssetAsync(Guid managerUserId, Guid assetId, AssetCorrection input, CancellationToken cancellationToken)
    {
        var reason = Text.Trim(input.Reason, 500);
        if (reason is null) return OperationResult<AssetView>.Fail(AfterSalesErrorCodes.ReasonRequired, "A reason for the correction is required.");
        var asset = await data.FindAssetAsync(assetId, false, cancellationToken);
        if (asset is null) return OperationResult<AssetView>.Fail(AfterSalesErrorCodes.AssetNotFound, "Asset was not found.");
        var manufacturer = Text.Trim(input.Manufacturer, 100) ?? asset.Manufacturer;
        var serial = Text.Trim(input.SerialNumber, 100) ?? asset.SerialNumber;
        if ((manufacturer != asset.Manufacturer || serial != asset.SerialNumber) && await data.SerialExistsAsync(manufacturer, serial, assetId, cancellationToken))
            return OperationResult<AssetView>.Fail(AfterSalesErrorCodes.DuplicateSerial, "Another asset already has this manufacturer and serial number.");
        try
        {
            var updated = await data.ExecuteInTransactionAsync<InstalledAsset>(async token =>
            {
                var tracked = (await data.FindAssetAsync(assetId, true, token))!;
                var before = $"{tracked.Manufacturer} | {tracked.Model} | {tracked.SerialNumber} | {tracked.PositionCode} | {tracked.Specifications}";
                tracked.Manufacturer = manufacturer;
                tracked.Model = Text.Trim(input.Model, 100) ?? tracked.Model;
                tracked.SerialNumber = serial;
                tracked.PositionCode = input.PositionCode is null ? tracked.PositionCode : Text.Trim(input.PositionCode, 50);
                tracked.Specifications = input.Specifications is null ? tracked.Specifications : Text.Trim(input.Specifications, 500);
                var after = $"{tracked.Manufacturer} | {tracked.Model} | {tracked.SerialNumber} | {tracked.PositionCode} | {tracked.Specifications}";
                AfterSalesHistory.Record(data, EventSubjects.Asset, assetId, "ASSET_CORRECTED", managerUserId, $"{reason}. before: {before}; after: {after}", time.GetUtcNow());
                await data.SaveChangesAsync(token);
                return (tracked, true);
            }, cancellationToken);
            return OperationResult<AssetView>.Ok(AfterSalesViews.Asset(updated));
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<AssetView>.Fail(AfterSalesErrorCodes.DuplicateSerial, "Another asset already has this manufacturer and serial number.");
        }
    }

    // ================================================================== helpers

    /// <summary>Cancels open visits, open quotations (support released) and open Sales tasks; frees a maintenance visit for re-booking.</summary>
    private async Task CloseEverythingAsync(ServiceRequest request, Guid actorUserId, string reason, DateTimeOffset now, CancellationToken token)
    {
        await AfterSalesWorkflow.CancelOpenVisitsAsync(data, request.Id, reason, now, token);
        foreach (var quotation in (await data.ListQuotationsAsync(request.Id, true, token)).Where(q => QuotationStatuses.IsOpen(q.Status)))
            await AfterSalesWorkflow.CloseQuotationAsync(data, quotation, QuotationStatuses.Cancelled, ItemStatuses.Declined, actorUserId, now, token);
        // Paid quotations of cancelled work give back their reserved disaster support (never consumed).
        foreach (var paid in (await data.ListQuotationsAsync(request.Id, true, token)).Where(q => q.Status == QuotationStatuses.Paid && q.DisasterSupportAmount > 0))
        {
            var claim = await data.FindClaimForRequestAsync(request.Id, true, token);
            var term = claim?.SubscriptionId is { } id ? await data.FindSubscriptionAsync(id, true, token) : null;
            if (claim is not null && term is not null) AfterSalesWorkflow.Release(claim, term, paid.DisasterSupportAmount, now);
            paid.Status = QuotationStatuses.Cancelled;
            paid.UpdatedAt = now;
            paid.Revision++;
        }
        await AfterSalesWorkflow.CloseTasksAsync(data, request.Id, now, token);
        if (request.MaintenanceEntitlementId is { } entitlementId && await data.FindEntitlementAsync(entitlementId, true, token) is { } entitlement
            && entitlement.CurrentRequestId == request.Id)
        {
            entitlement.Status = EntitlementStatuses.Available;
            entitlement.CurrentRequestId = null;
            entitlement.UpdatedAt = now;
            entitlement.Revision++;
        }
    }

    /// <summary>Records an approved manual refund of the paid quotations, minus the documented non-recoverable costs.</summary>
    private async Task RefundPaidQuotationsAsync(ServiceRequest request, Guid managerUserId, decimal nonRecoverable, string reason, DateTimeOffset now, CancellationToken token)
    {
        var paid = (await data.ListQuotationsAsync(request.Id, false, token)).Where(q => q.Status == QuotationStatuses.Paid).ToList();
        var total = paid.Sum(q => q.CustomerAmount);
        if (total <= 0) return;
        AddRefund(request, paid[0], total, Math.Max(0, total - nonRecoverable), reason, managerUserId, now);
    }

    private void AddRefund(ServiceRequest request, ServiceQuotation? quotation, decimal original, decimal approved, string reason, Guid managerUserId, DateTimeOffset now)
    {
        var refund = new RefundRecord
        {
            Id = Guid.NewGuid(), CustomerUserId = request.CustomerUserId, PaymentAttemptId = quotation?.CurrentPaymentAttemptId, QuotationId = quotation?.Id,
            RequestId = request.Id, OriginalAmount = original, ApprovedAmount = approved, Reason = Text.Trim(reason, 1000)!, Status = RefundStatuses.Approved,
            ApprovedByUserId = managerUserId, ApprovedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        data.Add(refund);
        AfterSalesHistory.Record(data, EventSubjects.Refund, refund.Id, "REFUND_APPROVED", managerUserId, $"{approved:0} of {original:0} VND: {refund.Reason}", now);
    }

    /// <summary>Loads the request tracked, checks the revision when given, applies <paramref name="change"/> and commits.</summary>
    private async Task<OperationResult<ServiceRequestView>> MutateAsync(Guid requestId, int? expectedRevision,
        Func<ServiceRequest, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var request = await data.FindRequestAsync(requestId, true, token);
                if (request is null) return (OperationResult<bool>.Fail(AfterSalesErrorCodes.RequestNotFound, "Request was not found."), false);
                if (expectedRevision is { } expected && request.Revision != expected) return (Stale<bool>(request.Revision), false);
                var outcome = await change(request, time.GetUtcNow(), token);
                if (!outcome.Succeeded) return (outcome, false);
                await data.SaveChangesAsync(token);
                return (outcome, true);
            }, cancellationToken);
            return result.Succeeded ? await ViewAsync(requestId, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.");
        }
    }

    private async Task<OperationResult<ServiceRequestView>> ViewAsync(Guid requestId, CancellationToken token)
        => OperationResult<ServiceRequestView>.Ok(await views.RequestAsync((await data.FindRequestAsync(requestId, false, token))!, forStaff: true, token));

    private static OperationResult<bool> Bad(string message) => OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, message);
    private static OperationResult<T> Stale<T>(int revision)
        => OperationResult<T>.Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.", new { currentRevision = revision });
    private static OperationResult<ServiceRequestView> Fail(string code, string message, object? details = null) => OperationResult<ServiceRequestView>.Fail(code, message, details);
    private static OperationResult<ServiceRequestView> NotFound() => Fail(AfterSalesErrorCodes.RequestNotFound, "Request was not found.");
}
