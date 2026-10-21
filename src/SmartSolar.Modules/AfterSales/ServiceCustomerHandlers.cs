using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

/// <summary>A customer-reported fault. Only the project and a description are required; the faulty asset is optional.</summary>
public sealed record CreateFaultInput(Guid ProjectId, string? Description, Guid? SuspectedAssetId, IReadOnlyList<Guid>? EvidenceFileIds,
    Guid? WarrantySourceRequestId, string? DisasterType, DateTimeOffset? IncidentOccurredAt);

/// <summary>
/// Customer side of Flow 3. Every operation checks that the caller owns the installation / request (otherwise 404, no existence
/// disclosure). Only the customer creates their requests and only the customer accepts completed work.
/// </summary>
public sealed class ServiceCustomerHandlers(
    IAfterSalesUnitOfWork data,
    FileService files,
    ISchedulingPolicyProvider policies,
    WorkingCalendar calendar,
    AfterSalesViews views,
    TimeProvider time)
{
    // ================================================================== installations and warranty

    /// <summary>Installation warranty (from handover, months copied into the contract), assets and purchased packages of an owned installation.</summary>
    public async Task<OperationResult<WarrantyView>> GetWarrantyAsync(Guid customerUserId, Guid projectId, CancellationToken cancellationToken)
    {
        var installation = await data.GetInstallationAsync(projectId, cancellationToken);
        if (installation is null || installation.CustomerUserId != customerUserId)
            return OperationResult<WarrantyView>.Fail(AfterSalesErrorCodes.ProjectNotFound, "Installation was not found.");
        var endsAt = WarrantyEnd(installation);
        var assets = (await data.ListAssetsAsync(projectId, false, cancellationToken)).Select(AfterSalesViews.Asset).ToList();
        var subscriptions = new List<SubscriptionView>();
        foreach (var s in await data.ListSubscriptionsForContractAsync(installation.ContractId, false, cancellationToken))
            subscriptions.Add(await views.SubscriptionAsync(s, cancellationToken));
        var underWarranty = endsAt is { } end && end > time.GetUtcNow();
        var status = installation.HandoverAt is null ? InstallationWarrantyStatuses.NotHandedOver
            : installation.WarrantyMonths is null ? InstallationWarrantyStatuses.NotRecorded
            : underWarranty ? InstallationWarrantyStatuses.Active : InstallationWarrantyStatuses.Expired;
        return OperationResult<WarrantyView>.Ok(new WarrantyView(projectId, installation.HandoverAt, installation.WarrantyMonths, endsAt, underWarranty, assets,
            subscriptions, status));
    }

    /// <summary>Handover + warranty months (contract snapshot); null when the project is not handed over or the contract has no warranty months.</summary>
    private DateTimeOffset? WarrantyEnd(InstallationFacts installation)
        => installation.HandoverAt is { } handover && installation.WarrantyMonths is { } months ? AfterSalesRules.AddMonths(handover, months, calendar.TimeZone) : null;

    // ================================================================== fault requests

    /// <summary>
    /// Creates a FAULT request for an owned, handed-over installation. Warranty eligibility is frozen now (the submission time decides,
    /// not later processing). Other open requests of the same installation are returned as a non-blocking duplicate warning. A disaster
    /// claim is linked to the protection term that covered the incident date; when none did, the claim is stored as NOT_ELIGIBLE.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> CreateFaultAsync(Guid customerUserId, CreateFaultInput input, CancellationToken cancellationToken,
        Guid? originRequestId = null)
    {
        var description = Text.Trim(input.Description, 2000);
        if (description is null || description.Length < 5) return Invalid("description", "Describe the problem (at least 5 characters).");
        var installation = await OwnedHandedOverAsync(customerUserId, input.ProjectId, cancellationToken);
        if (!installation.Succeeded) return installation.As<ServiceRequestView>();
        var facts = installation.Value!;
        if (input.SuspectedAssetId is { } assetId && (await data.FindAssetAsync(assetId, false, cancellationToken))?.ProjectId != facts.ProjectId)
            return Invalid("suspectedAssetId", "The equipment does not belong to this installation.");

        var now = time.GetUtcNow();
        var warrantyEnd = WarrantyEnd(facts);
        var withinRepairWarranty = false;
        if (input.WarrantySourceRequestId is { } sourceId)
        {
            var source = await data.FindRequestAsync(sourceId, false, cancellationToken);
            if (source is null || source.ProjectId != facts.ProjectId) return Invalid("warrantySourceRequestId", "The earlier repair was not found on this installation.");
            withinRepairWarranty = source.RepairWarrantyEndsAt is { } repairEnd && repairEnd > now;
        }

        DisasterClaim? claim = null;
        if (input.DisasterType is not null)
        {
            if (!DisasterTypes.All.Contains(input.DisasterType)) return Invalid("disasterType", $"Use one of {string.Join(", ", DisasterTypes.All)}.");
            if (input.IncidentOccurredAt is not { } incident || incident > now) return Invalid("incidentOccurredAt", "Give the date the incident happened (not in the future).");
            claim = await NewClaimAsync(facts.ContractId, input.DisasterType, incident.ToUniversalTime(), now, cancellationToken);
        }

        var evidence = await VerifiedEvidenceAsync(customerUserId, input.EvidenceFileIds ?? [], cancellationToken);
        if (!evidence.Succeeded) return evidence.As<ServiceRequestView>();

        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(), RequestNumber = AfterSalesWorkflow.NewRequestNumber(now), Kind = ServiceRequestKinds.Fault, Status = ServiceRequestStatuses.Submitted,
            CustomerUserId = customerUserId, ProjectId = facts.ProjectId, PropertySiteId = facts.PropertySiteId, Description = description,
            SuspectedAssetId = input.SuspectedAssetId, InstallationWarrantyEndsAt = warrantyEnd, WithinInstallationWarranty = warrantyEnd is { } end && end > now,
            WarrantySourceRequestId = input.WarrantySourceRequestId, WithinRepairWarranty = withinRepairWarranty, OriginRequestId = originRequestId,
            CreatedAt = now, UpdatedAt = now,
        };
        await data.ExecuteInTransactionAsync<bool>(async token =>
        {
            data.Add(request);
            if (claim is not null)
            {
                claim.RequestId = request.Id;
                data.Add(claim);
            }
            foreach (var fileId in evidence.Value!)
                data.Add(new ServiceEvidence { Id = Guid.NewGuid(), RequestId = request.Id, FileId = fileId, AddedByUserId = customerUserId, AddedAt = now });
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "REQUEST_CREATED", customerUserId,
                $"warranty until {warrantyEnd:O}; within warranty: {request.WithinInstallationWarranty}" + (claim is null ? "" : $"; disaster claim {claim.Status}"), now);
            AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "FAULT_REPORTED",
                $"New fault request {request.RequestNumber}.", now, request.Id);
            await data.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        return OperationResult<ServiceRequestView>.Ok(await views.RequestAsync(request, forStaff: false, cancellationToken));
    }

    /// <summary>The term that covered the incident (continues to apply after it expires or is renewed), or a NOT_ELIGIBLE claim with the reason.</summary>
    private async Task<DisasterClaim> NewClaimAsync(Guid contractId, string disasterType, DateTimeOffset incidentAt, DateTimeOffset now, CancellationToken token)
    {
        var terms = (await data.ListSubscriptionsForContractAsync(contractId, false, token))
            .Where(s => s.Kind == ServicePackageKinds.DisasterProtection && s.StartsAt is not null && s.Status is SubscriptionStatuses.Active or SubscriptionStatuses.Expired)
            .ToList();
        var covering = terms.FirstOrDefault(t => t.StartsAt <= incidentAt && incidentAt < t.EndsAt);
        var problem = covering is null ? "No disaster-protection term covered the incident date." : AfterSalesRules.DisasterIneligibility(covering, disasterType, incidentAt, now);
        return new DisasterClaim
        {
            Id = Guid.NewGuid(), SubscriptionId = covering?.Id, DisasterType = disasterType, IncidentOccurredAt = incidentAt,
            Status = problem is null ? ClaimStatuses.Submitted : ClaimStatuses.NotEligible, DecisionReason = problem,
            CoveragePercent = covering?.CoveragePercent ?? 0, PerIncidentLimit = covering?.PerIncidentLimit ?? 0, CreatedAt = now, UpdatedAt = now,
        };
    }

    /// <summary>The customer asks to handle a problem the technician noticed during maintenance: a separate FAULT request linked to that visit.</summary>
    public async Task<OperationResult<ServiceRequestView>> ReportFaultFromMaintenanceAsync(Guid customerUserId, Guid maintenanceRequestId, string? description,
        CancellationToken cancellationToken)
    {
        var maintenance = await OwnedAsync(customerUserId, maintenanceRequestId, cancellationToken);
        if (maintenance is null || maintenance.Kind == ServiceRequestKinds.Fault) return NotFound();
        var finding = (await data.ListVisitsAsync(maintenance.Id, false, cancellationToken)).LastOrDefault(v => v.FindingNote is not null)?.FindingNote;
        var text = Text.Trim(description, 2000) ?? (finding is null ? null : $"Found during maintenance: {finding}");
        return await CreateFaultAsync(customerUserId, new CreateFaultInput(maintenance.ProjectId, text, null, null, null, null, null), cancellationToken, maintenance.Id);
    }

    public async Task<IReadOnlyList<ServiceRequestSummary>> ListAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await data.ListRequestsAsync(new RequestFilter(CustomerUserId: customerUserId), cancellationToken)).Select(views.Summary).ToList();

    public async Task<OperationResult<ServiceRequestView>> GetAsync(Guid customerUserId, Guid requestId, CancellationToken cancellationToken)
        => await OwnedAsync(customerUserId, requestId, cancellationToken) is { } request
            ? OperationResult<ServiceRequestView>.Ok(await views.RequestAsync(request, forStaff: false, cancellationToken))
            : NotFound();

    // ================================================================== cancellation

    /// <summary>
    /// Free work not started yet (no visit in progress, nothing paid): cancelled at once with the reason (open visit, quotations and tasks are
    /// released). Paid work not started: a cancellation REQUEST that a Manager decides (a refund, if any, is manual). Started work: the
    /// customer cannot cancel online. Maintenance appointments can be changed only until the configured lead time before their start.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> CancelAsync(Guid customerUserId, Guid requestId, string? reason, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 1000);
        if (why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "A cancellation reason is required.");
        if (await OwnedAsync(customerUserId, requestId, cancellationToken) is null) return NotFound();
        var policy = await policies.GetAsync(cancellationToken);
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var request = (await data.FindRequestAsync(requestId, true, token))!;
                var now = time.GetUtcNow();
                if (ServiceRequestStatuses.IsFinal(request.Status)) return (Bad("The request is already closed."), false);
                if (request.CancellationRequestedAt is not null) return (Bad("A cancellation request is already waiting for a Manager."), false);
                var visits = await data.ListVisitsAsync(requestId, false, token);
                if (visits.Any(v => v.Status is VisitStatuses.InProgress) || request.Status is ServiceRequestStatuses.InProgress
                        or ServiceRequestStatuses.PendingCustomerAcceptance or ServiceRequestStatuses.Disputed or ServiceRequestStatuses.ReworkRequired)
                    return (Bad("Work has started; please contact us so a Manager can review a stop."), false);
                if (request.Kind != ServiceRequestKinds.Fault
                    && visits.FirstOrDefault(v => v.Status == VisitStatuses.Scheduled) is { } scheduled
                    && scheduled.StartsAt < now.AddHours(policy.Get(AfterSalesSettingKeys.AppointmentChangeLeadHours)))
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.ChangeTooLate,
                        $"Appointments can be changed online until {policy.Get(AfterSalesSettingKeys.AppointmentChangeLeadHours)} hours before the start."), false);

                if ((await data.ListQuotationsAsync(requestId, false, token)).Any(q => q.Status == QuotationStatuses.Paid))
                {
                    // Paid work: a Manager decides (and may keep documented non-recoverable costs).
                    request.CancellationRequestedAt = now;
                    request.CancellationReason = why;
                    request.UpdatedAt = now;
                    request.Revision++;
                    AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "CANCELLATION_REQUESTED", customerUserId, why, now);
                    AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "CANCELLATION_REQUESTED",
                        $"{request.RequestNumber}: the customer asks to cancel paid work.", now, requestId);
                }
                else
                {
                    await AfterSalesWorkflow.CancelOpenVisitsAsync(data, requestId, "cancelled by customer", now, token);
                    foreach (var quotation in (await data.ListQuotationsAsync(requestId, true, token)).Where(q => QuotationStatuses.IsOpen(q.Status)))
                        await AfterSalesWorkflow.CloseQuotationAsync(data, quotation, QuotationStatuses.Cancelled, ItemStatuses.Declined, customerUserId, now, token);
                    await AfterSalesWorkflow.CloseTasksAsync(data, requestId, now, token);
                    await FreeEntitlementAsync(request, now, token);
                    request.CancellationReason = why;
                    request.CancelledAt = now;
                    AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Cancelled, customerUserId, why, now);
                }
                await data.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            return result.Succeeded ? await GetAsync(customerUserId, requestId, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.");
        }
    }

    // ================================================================== completion

    /// <summary>
    /// The customer's final decision on completed work (only the customer can accept). Acceptance closes the request and, in the same
    /// transaction: consumes the maintenance visit, finalizes reserved disaster support of paid quotations, and activates replacement
    /// equipment (the replaced asset is kept as history). A rejection needs a reason and goes to a Manager.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> DecideCompletionAsync(Guid customerUserId, Guid requestId, bool accept, string? reason,
        IReadOnlyList<Guid>? evidenceFileIds, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 1000);
        if (!accept && why is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "Tell us what is wrong with the work.");
        if (await OwnedAsync(customerUserId, requestId, cancellationToken) is null) return NotFound();
        var evidence = await VerifiedEvidenceAsync(customerUserId, evidenceFileIds ?? [], cancellationToken);
        if (!evidence.Succeeded) return evidence.As<ServiceRequestView>();
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var request = (await data.FindRequestAsync(requestId, true, token))!;
                if (request.Status != ServiceRequestStatuses.PendingCustomerAcceptance) return (Bad("Nothing is waiting for your acceptance."), false);
                var now = time.GetUtcNow();
                request.CustomerDecisionReason = why;
                foreach (var fileId in evidence.Value!)
                    data.Add(new ServiceEvidence { Id = Guid.NewGuid(), RequestId = requestId, FileId = fileId, Caption = "customer", AddedByUserId = customerUserId, AddedAt = now });
                if (!accept)
                {
                    AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Disputed, customerUserId, why, now);
                    AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "COMPLETION_REJECTED",
                        $"{request.RequestNumber}: the customer rejected the work.", now, requestId);
                }
                else
                {
                    await FinalizeAcceptedAsync(request, customerUserId, now, token);
                    request.CompletedAt = now;
                    AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.Completed, customerUserId, "accepted by customer", now);
                }
                await data.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            return result.Succeeded ? await GetAsync(customerUserId, requestId, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.");
        }
    }

    private async Task FinalizeAcceptedAsync(ServiceRequest request, Guid customerUserId, DateTimeOffset now, CancellationToken token)
    {
        // A maintenance visit counts as used only now (never on silence, absence or a rejected result).
        if (request.MaintenanceEntitlementId is { } entitlementId)
        {
            var entitlement = (await data.FindEntitlementAsync(entitlementId, true, token))!;
            entitlement.Status = EntitlementStatuses.Consumed;
            entitlement.ConsumedAt = now;
            entitlement.UpdatedAt = now;
            entitlement.Revision++;
            AfterSalesHistory.Record(data, EventSubjects.Subscription, entitlement.SubscriptionId, "VISIT_CONSUMED", customerUserId, $"plan month {entitlement.PlanMonth}", now);
        }

        // Reserved disaster support of the paid work becomes consumed support of the term that covered the incident.
        var claim = await data.FindClaimForRequestAsync(request.Id, true, token);
        var term = claim?.SubscriptionId is { } termId ? await data.FindSubscriptionAsync(termId, true, token) : null;
        if (claim is not null && term is not null)
            foreach (var paid in (await data.ListQuotationsAsync(request.Id, false, token)).Where(q => q.Status == QuotationStatuses.Paid && q.DisasterSupportAmount > 0))
                AfterSalesWorkflow.Consume(claim, term, paid.DisasterSupportAmount, now);

        // Replacement equipment becomes the active asset; the replaced asset stays as history.
        var assets = await data.ListAssetsAsync(request.ProjectId, true, token);
        foreach (var replacement in assets.Where(a => a.ServiceRequestId == request.Id && a.Status == InstalledAssetStatuses.PendingAcceptance))
        {
            replacement.Status = InstalledAssetStatuses.Active;
            if (assets.FirstOrDefault(a => a.Id == replacement.ReplacesAssetId) is { } old) old.Status = InstalledAssetStatuses.Replaced;
            AfterSalesHistory.Record(data, EventSubjects.Asset, replacement.Id, "ASSET_REPLACEMENT_ACTIVATED", customerUserId,
                $"{replacement.SerialNumber} replaces {replacement.ReplacesAssetId}", now);
        }
        await AfterSalesWorkflow.CloseTasksAsync(data, request.Id, now, token);
    }

    // ================================================================== disaster reconsideration and appointments

    /// <summary>Asks once for reconsideration of a rejected disaster claim, with additional evidence.</summary>
    public async Task<OperationResult<ServiceRequestView>> RequestReconsiderationAsync(Guid customerUserId, Guid requestId, string? note,
        IReadOnlyList<Guid>? evidenceFileIds, CancellationToken cancellationToken)
    {
        var text = Text.Trim(note, 1000);
        if (text is null) return Fail(AfterSalesErrorCodes.ReasonRequired, "Explain what the additional evidence shows.");
        if (await OwnedAsync(customerUserId, requestId, cancellationToken) is null) return NotFound();
        var evidence = await VerifiedEvidenceAsync(customerUserId, evidenceFileIds ?? [], cancellationToken);
        if (!evidence.Succeeded) return evidence.As<ServiceRequestView>();
        var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
        {
            var claim = await data.FindClaimForRequestAsync(requestId, true, token);
            if (claim is null || claim.Status != ClaimStatuses.Rejected) return (Bad("Only a rejected disaster claim can be reconsidered."), false);
            if (claim.ReconsiderationRequestedAt is not null) return (OperationResult<bool>.Fail(AfterSalesErrorCodes.ReconsiderationUsed, "Reconsideration was already used."), false);
            var now = time.GetUtcNow();
            claim.Status = ClaimStatuses.ReconsiderationRequested;
            claim.ReconsiderationRequestedAt = now;
            claim.ReconsiderationNote = text;
            claim.UpdatedAt = now;
            claim.Revision++;
            foreach (var fileId in evidence.Value!)
                data.Add(new ServiceEvidence { Id = Guid.NewGuid(), RequestId = requestId, FileId = fileId, Caption = "reconsideration", AddedByUserId = customerUserId, AddedAt = now });
            AfterSalesHistory.Record(data, EventSubjects.Claim, claim.Id, "RECONSIDERATION_REQUESTED", customerUserId, text, now);
            AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "RECONSIDERATION_REQUESTED",
                "A customer asked to reconsider a disaster-protection decision.", now, requestId);
            await data.SaveChangesAsync(token);
            return (OperationResult<bool>.Ok(true), true);
        }, cancellationToken);
        return result.Succeeded ? await GetAsync(customerUserId, requestId, cancellationToken) : result.As<ServiceRequestView>();
    }

    /// <summary>Confirms a visit time the Manager proposed instead of the customer's preferred time.</summary>
    public async Task<OperationResult<ServiceRequestView>> ConfirmAppointmentAsync(Guid customerUserId, Guid requestId, CancellationToken cancellationToken)
    {
        if (await OwnedAsync(customerUserId, requestId, cancellationToken) is null) return NotFound();
        var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
        {
            var visit = (await data.ListVisitsAsync(requestId, true, token)).FirstOrDefault(v => v.Status == VisitStatuses.Scheduled && v.CustomerConfirmationRequired);
            if (visit is null) return (Bad("No proposed appointment is waiting for your confirmation."), false);
            var now = time.GetUtcNow();
            visit.CustomerConfirmedAt ??= now;
            visit.UpdatedAt = now;
            AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "APPOINTMENT_CONFIRMED", customerUserId, $"{visit.StartsAt:O}", now);
            await data.SaveChangesAsync(token);
            return (OperationResult<bool>.Ok(true), true);
        }, cancellationToken);
        return result.Succeeded ? await GetAsync(customerUserId, requestId, cancellationToken) : result.As<ServiceRequestView>();
    }

    // ================================================================== maintenance bookings

    /// <summary>
    /// Books one included maintenance visit for a preferred time (a request, not a confirmed appointment). The time must be at least the
    /// booking lead (default 7 days) ahead, not before the visit's plan-month period, and before the term ends; a missed month may be
    /// booked later while the term is active. Visits never move to another term.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> BookMaintenanceAsync(Guid customerUserId, Guid entitlementId, DateTimeOffset preferredStartsAt,
        string? note, CancellationToken cancellationToken)
    {
        var entitlement = await data.FindEntitlementAsync(entitlementId, false, cancellationToken);
        var term = entitlement is null ? null : await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken);
        if (entitlement is null || term is null || term.CustomerUserId != customerUserId)
            return Fail(AfterSalesErrorCodes.EntitlementNotFound, "Maintenance visit was not found.");
        if (term.Status != SubscriptionStatuses.Active || term.ProjectId is null) return Fail(AfterSalesErrorCodes.InvalidState, "The maintenance plan is not active.");
        if (entitlement.Status is not (EntitlementStatuses.Available or EntitlementStatuses.NotCompleted))
            return Fail(AfterSalesErrorCodes.InvalidState, $"This visit is {entitlement.Status}.");
        var start = preferredStartsAt.ToUniversalTime();
        var check = await BookingProblemAsync(entitlement, term, start, cancellationToken);
        if (check is not null) return check;
        var installation = (await data.GetInstallationAsync(term.ProjectId.Value, cancellationToken))!;

        var now = time.GetUtcNow();
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(), RequestNumber = AfterSalesWorkflow.NewRequestNumber(now), Kind = ServiceRequestKinds.Maintenance, Status = ServiceRequestStatuses.Submitted,
            CustomerUserId = customerUserId, ProjectId = installation.ProjectId, PropertySiteId = installation.PropertySiteId,
            Description = Text.Trim(note, 2000) ?? $"Maintenance visit (plan month {entitlement.PlanMonth})", PreferredStartsAt = start,
            MaintenanceEntitlementId = entitlementId, CreatedAt = now, UpdatedAt = now,
        };
        bool booked;
        try
        {
            booked = await data.ExecuteInTransactionAsync<bool>(async token =>
            {
                var tracked = (await data.FindEntitlementAsync(entitlementId, true, token))!;
                if (tracked.Status is not (EntitlementStatuses.Available or EntitlementStatuses.NotCompleted)) return (false, false);
                data.Add(request);
                tracked.Status = EntitlementStatuses.Booked;
                tracked.CurrentRequestId = request.Id;
                tracked.UpdatedAt = now;
                tracked.Revision++;
                // A booking answers the Sales follow-up of this visit.
                if (await data.FindOpenTaskAsync(null, entitlementId, SalesTaskTypes.MaintenanceFollowUp, true, token) is { } followUp)
                {
                    followUp.Status = SalesTaskStatuses.Done;
                    followUp.Outcome = FollowUpOutcomes.Booked;
                    followUp.CompletedAt = now;
                    followUp.UpdatedAt = now;
                    followUp.Revision++;
                }
                AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "MAINTENANCE_BOOKED", customerUserId, $"plan month {tracked.PlanMonth}, preferred {start:O}", now);
                AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "MAINTENANCE_BOOKED",
                    $"Maintenance booking {request.RequestNumber} needs a technician.", now, request.Id);
                await data.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            booked = false;   // the unique "one live booking per visit" index or the entitlement revision caught a parallel booking
        }
        if (!booked) return Fail(AfterSalesErrorCodes.InvalidState, "This visit was booked meanwhile.");
        return OperationResult<ServiceRequestView>.Ok(await views.RequestAsync(request, forStaff: false, cancellationToken));
    }

    /// <summary>Changes the preferred time of a maintenance booking (same rules as booking; at least the change lead before a planned visit).</summary>
    public async Task<OperationResult<ServiceRequestView>> RescheduleMaintenanceAsync(Guid customerUserId, Guid requestId, DateTimeOffset preferredStartsAt,
        CancellationToken cancellationToken)
    {
        var request = await OwnedAsync(customerUserId, requestId, cancellationToken);
        if (request is null || request.Kind == ServiceRequestKinds.Fault) return NotFound();
        if (request.Status is not (ServiceRequestStatuses.Submitted or ServiceRequestStatuses.Scheduled or ServiceRequestStatuses.ReadyForWork))
            return Fail(AfterSalesErrorCodes.InvalidState, "This appointment can no longer be changed online.");
        var start = preferredStartsAt.ToUniversalTime();
        var policy = await policies.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var scheduled = (await data.ListVisitsAsync(requestId, false, cancellationToken)).FirstOrDefault(v => v.Status == VisitStatuses.Scheduled);
        if (scheduled is not null && scheduled.StartsAt < now.AddHours(policy.Get(AfterSalesSettingKeys.AppointmentChangeLeadHours)))
            return Fail(AfterSalesErrorCodes.ChangeTooLate, $"Appointments can be changed online until {policy.Get(AfterSalesSettingKeys.AppointmentChangeLeadHours)} hours before the start.");
        if (request.MaintenanceEntitlementId is { } entitlementId)
        {
            var entitlement = (await data.FindEntitlementAsync(entitlementId, false, cancellationToken))!;
            var term = (await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken))!;
            if (await BookingProblemAsync(entitlement, term, start, cancellationToken) is { } problem) return problem;
        }
        else if (!AfterSalesRules.RespectsLead(start, now, policy.Get(AfterSalesSettingKeys.MaintenanceBookingLeadDays)))
        {
            return Fail(AfterSalesErrorCodes.BookingTooSoon, $"Book at least {policy.Get(AfterSalesSettingKeys.MaintenanceBookingLeadDays)} days ahead.");
        }
        var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
        {
            var tracked = (await data.FindRequestAsync(requestId, true, token))!;
            await AfterSalesWorkflow.CancelOpenVisitsAsync(data, requestId, "rescheduled by customer", now, token);
            tracked.PreferredStartsAt = start;
            AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "APPOINTMENT_CHANGED", customerUserId, $"new preferred start {start:O}", now);
            // A planned visit is replanned by a Manager for the new time.
            var back = tracked.Kind == ServiceRequestKinds.Maintenance ? ServiceRequestStatuses.Submitted
                : tracked.Status == ServiceRequestStatuses.Scheduled ? ServiceRequestStatuses.ReadyForWork : tracked.Status;
            AfterSalesWorkflow.Move(data, tracked, back, customerUserId, "rescheduled", now);
            await data.SaveChangesAsync(token);
            return (OperationResult<bool>.Ok(true), true);
        }, cancellationToken);
        return result.Succeeded ? await GetAsync(customerUserId, requestId, cancellationToken) : result.As<ServiceRequestView>();
    }

    private async Task<OperationResult<ServiceRequestView>?> BookingProblemAsync(MaintenanceEntitlement entitlement, ServiceSubscription term, DateTimeOffset start,
        CancellationToken token)
    {
        var policy = await policies.GetAsync(token);
        var lead = policy.Get(AfterSalesSettingKeys.MaintenanceBookingLeadDays);
        if (!AfterSalesRules.RespectsLead(start, time.GetUtcNow(), lead)) return Fail(AfterSalesErrorCodes.BookingTooSoon, $"Book at least {lead} days ahead.");
        var latest = entitlement.ExtendedUntil ?? term.EndsAt!.Value;
        if (start < entitlement.PeriodStartsAt || start >= latest)
            return Fail(AfterSalesErrorCodes.OutsidePeriod, $"This visit can be booked from {entitlement.PeriodStartsAt:O} until the plan ends ({latest:O}).");
        return null;
    }

    private async Task FreeEntitlementAsync(ServiceRequest request, DateTimeOffset now, CancellationToken token)
    {
        if (request.MaintenanceEntitlementId is not { } entitlementId) return;
        var entitlement = (await data.FindEntitlementAsync(entitlementId, true, token))!;
        if (entitlement.CurrentRequestId != request.Id) return;
        entitlement.Status = EntitlementStatuses.Available;
        entitlement.CurrentRequestId = null;
        entitlement.UpdatedAt = now;
        entitlement.Revision++;
    }

    // ================================================================== one-time maintenance

    /// <summary>
    /// Asks for a single paid maintenance (no subscription needed; never uses subscription visits). Sales prices it per job; after the
    /// customer accepts and pays the quotation, a Manager schedules the technician.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> RequestOneTimeMaintenanceAsync(Guid customerUserId, Guid projectId, DateTimeOffset preferredStartsAt,
        string? note, CancellationToken cancellationToken)
    {
        var installation = await OwnedHandedOverAsync(customerUserId, projectId, cancellationToken);
        if (!installation.Succeeded) return installation.As<ServiceRequestView>();
        var policy = await policies.GetAsync(cancellationToken);
        var lead = policy.Get(AfterSalesSettingKeys.MaintenanceBookingLeadDays);
        var now = time.GetUtcNow();
        var start = preferredStartsAt.ToUniversalTime();
        if (!AfterSalesRules.RespectsLead(start, now, lead)) return Fail(AfterSalesErrorCodes.BookingTooSoon, $"Book at least {lead} days ahead.");
        var facts = installation.Value!;
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(), RequestNumber = AfterSalesWorkflow.NewRequestNumber(now), Kind = ServiceRequestKinds.OneTimeMaintenance,
            Status = ServiceRequestStatuses.AwaitingQuotation, CustomerUserId = customerUserId, ProjectId = facts.ProjectId, PropertySiteId = facts.PropertySiteId,
            Description = Text.Trim(note, 2000) ?? "One-time maintenance", PreferredStartsAt = start, CreatedAt = now, UpdatedAt = now,
        };
        await data.ExecuteInTransactionAsync<bool>(async token =>
        {
            data.Add(request);
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "ONE_TIME_MAINTENANCE_REQUESTED", customerUserId, $"preferred {start:O}", now);
            await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.MaintenanceQuotation, customerUserId, request.Id, null, "Price the one-time maintenance.", now, token);
            await data.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        return OperationResult<ServiceRequestView>.Ok(await views.RequestAsync(request, forStaff: false, cancellationToken));
    }

    public async Task<IReadOnlyList<NotificationView>> NotificationsAsync(Guid userId, CancellationToken cancellationToken)
        => (await data.ListNotificationsAsync(userId, cancellationToken)).Select(views.Notification).ToList();

    // ================================================================== helpers

    private async Task<ServiceRequest?> OwnedAsync(Guid customerUserId, Guid requestId, CancellationToken token)
        => await data.FindRequestAsync(requestId, false, token) is { } request && request.CustomerUserId == customerUserId ? request : null;

    private async Task<OperationResult<InstallationFacts>> OwnedHandedOverAsync(Guid customerUserId, Guid projectId, CancellationToken token)
    {
        var installation = await data.GetInstallationAsync(projectId, token);
        if (installation is null || installation.CustomerUserId != customerUserId)
            return OperationResult<InstallationFacts>.Fail(AfterSalesErrorCodes.ProjectNotFound, "Installation was not found.");
        // Problems before handover belong to the installation completion workflow (rework), not to after-sales.
        if (installation.ProjectStatus != InstallationStatuses.Completed || installation.HandoverAt is null)
            return OperationResult<InstallationFacts>.Fail(AfterSalesErrorCodes.NotHandedOver, "After-sales services start after the installation is handed over.");
        return OperationResult<InstallationFacts>.Ok(installation);
    }

    /// <summary>Customer photos must be verified SERVICE_EVIDENCE files they uploaded, each used once.</summary>
    private async Task<OperationResult<IReadOnlyList<Guid>>> VerifiedEvidenceAsync(Guid uploaderUserId, IReadOnlyList<Guid> fileIds, CancellationToken token)
    {
        if (fileIds.Count == 0) return OperationResult<IReadOnlyList<Guid>>.Ok([]);
        if (fileIds.Count > 10) return OperationResult<IReadOnlyList<Guid>>.Fail(AfterSalesErrorCodes.ValidationFailed, "At most 10 photos at once.");
        var verified = await files.RequireVerifiedAsync(fileIds, FilePurposes.ServiceEvidence, uploaderUserId, token);
        if (!verified.Succeeded) return verified.As<IReadOnlyList<Guid>>();
        foreach (var id in fileIds)
            if (await data.IsEvidenceFileUsedAsync(id, token))
                return OperationResult<IReadOnlyList<Guid>>.Fail(AfterSalesErrorCodes.ValidationFailed, "A photo can be attached only once.");
        return OperationResult<IReadOnlyList<Guid>>.Ok(fileIds.Distinct().ToList());
    }

    private static OperationResult<bool> Bad(string message) => OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, message);
    private static OperationResult<ServiceRequestView> Fail(string code, string message) => OperationResult<ServiceRequestView>.Fail(code, message);
    private static OperationResult<ServiceRequestView> NotFound() => Fail(AfterSalesErrorCodes.RequestNotFound, "Request was not found.");
    private static OperationResult<ServiceRequestView> Invalid(string field, string message)
        => OperationResult<ServiceRequestView>.Fail(AfterSalesErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, string[]> { [field] = [message] });
}
