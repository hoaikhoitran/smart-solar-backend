using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.AfterSales;

public sealed record DiagnosedItemInput(Guid? AssetId, string? Description, string? Diagnosis, string? RequiredParts);

public sealed record ItemResultInput(Guid ItemId, string? WorkResult, string? PartsUsed);

/// <summary>A complete replacement of an installed asset: the old record is kept, the new one waits for the customer's acceptance.</summary>
public sealed record ReplacementInput(Guid ReplacedAssetId, string? Manufacturer, string? Model, string? SerialNumber, string? Specifications);

public sealed record TechnicianVisitView(Guid VisitId, Guid RequestId, string RequestNumber, string RequestKind, string Purpose, string Status, string? AssignmentStatus,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool CustomerConfirmationRequired, DateTimeOffset? CustomerConfirmedAt, string? SiteAddress, string Description,
    IReadOnlyList<ServiceItemView> Items, IReadOnlyList<AssetView> Assets, bool WaitingOnSite, IReadOnlyList<ServiceEvidenceView> Evidence);

/// <summary>
/// Technician side: accept or decline an assigned visit, start it, add verified photos, report the diagnosis (or no fault), optionally
/// stay on site and continue as the repair in the same visit, add items found during repair, report the completed work (parts used,
/// replacements) and report customer absence. A technician only sees
/// and changes visits assigned to them.
/// </summary>
public sealed class TechnicianServiceHandlers(IAfterSalesUnitOfWork data, FileService files, WorkingCalendar calendar, TimeProvider time)
{
    /// <summary>How long before the planned start the technician may start the visit.</summary>
    public static readonly TimeSpan EarlyStart = TimeSpan.FromHours(2);

    public async Task<IReadOnlyList<TechnicianVisitView>> ListAsync(Guid technicianUserId, CancellationToken cancellationToken)
    {
        var list = new List<TechnicianVisitView>();
        foreach (var visit in (await data.ListVisitsForTechnicianAsync(technicianUserId, cancellationToken)).Where(v => v.Status != VisitStatuses.Cancelled))
            list.Add(await ViewAsync(visit, cancellationToken));
        return list;
    }

    public async Task<OperationResult<TechnicianVisitView>> GetAsync(Guid technicianUserId, Guid visitId, CancellationToken cancellationToken)
        => await data.FindVisitAsync(visitId, false, cancellationToken) is { } visit && visit.TechnicianUserId == technicianUserId
            ? OperationResult<TechnicianVisitView>.Ok(await ViewAsync(visit, cancellationToken))
            : NotFound();

    // ================================================================== assignment

    /// <summary>Accepts the assignment, or declines it with a reason (the request goes back to the Manager for another technician).</summary>
    public Task<OperationResult<TechnicianVisitView>> RespondAsync(Guid technicianUserId, Guid visitId, bool accept, string? reason, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 500);
        if (!accept && why is null) return Task.FromResult(Fail(AfterSalesErrorCodes.ReasonRequired, "A decline reason is required."));
        return MutateAsync(technicianUserId, visitId, async (visit, assignment, request, now, token) =>
        {
            if (assignment?.Status != AssignmentStatuses.Pending || visit.Status != VisitStatuses.Scheduled) return Bad("This assignment is not waiting for your answer.");
            assignment.RespondedAt = now;
            if (accept)
            {
                assignment.Status = AssignmentStatuses.Accepted;
                AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "VISIT_ACCEPTED", technicianUserId, $"{visit.Purpose} visit", now);
                return OperationResult<bool>.Ok(true);
            }
            assignment.Status = AssignmentStatuses.Declined;
            assignment.ResponseReason = why;
            assignment.ClosedAt = now;
            visit.Status = VisitStatuses.Declined;
            AfterSalesWorkflow.Move(data, request, StatusBefore(visit, request), technicianUserId, $"visit declined: {why}", now);
            AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "VISIT_DECLINED",
                $"{request.RequestNumber}: the technician declined the visit ({why}).", now, request.Id);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Starts an accepted visit (from two hours before its planned start). A repair can only start on authorized items, so chargeable
    /// work never starts before its quotation is paid; a time the Manager proposed must have been confirmed by the customer.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> StartAsync(Guid technicianUserId, Guid visitId, CancellationToken cancellationToken)
        => MutateAsync(technicianUserId, visitId, async (visit, assignment, request, now, token) =>
        {
            if (assignment?.Status != AssignmentStatuses.Accepted || visit.Status != VisitStatuses.Scheduled) return Bad("Accept the visit first.");
            if (visit.CustomerConfirmationRequired && visit.CustomerConfirmedAt is null) return Bad("The customer has not confirmed this appointment yet.");
            if (now < visit.StartsAt - EarlyStart) return Bad("The visit cannot start this early.");
            if (visit.Purpose == VisitPurposes.Repair && !(await data.ListItemsAsync(request.Id, false, token)).Any(i => i.Status == ItemStatuses.Authorized))
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.PaymentRequired, "No authorized work: chargeable items must be accepted and paid first.");
            visit.Status = VisitStatuses.InProgress;
            visit.StartedAt = now;
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.InProgress, technicianUserId, $"{visit.Purpose} visit started", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Adds a verified SERVICE_EVIDENCE photo uploaded by the technician to an open visit.</summary>
    public async Task<OperationResult<TechnicianVisitView>> AddEvidenceAsync(Guid technicianUserId, Guid visitId, Guid fileId, string? caption, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.ServiceEvidence, technicianUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<TechnicianVisitView>();
        if (await data.IsEvidenceFileUsedAsync(fileId, cancellationToken)) return Fail(AfterSalesErrorCodes.ValidationFailed, "This photo is already attached.");
        return await MutateAsync(technicianUserId, visitId, (visit, _, request, now, _) =>
        {
            if (!VisitStatuses.IsOpen(visit.Status)) return Task.FromResult(Bad("Photos are added while the visit is open."));
            data.Add(new ServiceEvidence { Id = Guid.NewGuid(), RequestId = request.Id, VisitId = visit.Id, FileId = fileId, Caption = Text.Trim(caption, 300),
                AddedByUserId = technicianUserId, AddedAt = now });
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    // ================================================================== reports

    /// <summary>
    /// Reports an inspection: the diagnosed items (a Manager then decides coverage per item) or no fault found (the customer only has to
    /// confirm; nothing is charged). At least one photo of the visit is required. With <paramref name="stayOnSite"/> the technician keeps
    /// the visit open to repair during the same visit once the work is authorized (see <see cref="ContinueWithRepairAsync"/>).
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> ReportInspectionAsync(Guid technicianUserId, Guid visitId, string? report, bool noFaultFound,
        IReadOnlyList<DiagnosedItemInput> items, bool stayOnSite, CancellationToken cancellationToken)
    {
        if (noFaultFound && stayOnSite) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "Nothing to repair when no fault was found."));
        var text = Text.Trim(report, 4000);
        if (text is null) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "Describe what you inspected and found."));
        if (noFaultFound == (items.Count > 0)) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "Report either diagnosed items or no fault found."));
        return MutateAsync(technicianUserId, visitId, async (visit, assignment, request, now, token) =>
        {
            if (visit.Purpose != VisitPurposes.Inspection || visit.Status != VisitStatuses.InProgress) return Bad("Start the inspection first.");
            if (visit.Report is not null) return Bad("The diagnosis of this visit was already reported.");
            if (!await HasEvidenceAsync(request.Id, visit.Id, token)) return OperationResult<bool>.Fail(AfterSalesErrorCodes.EvidenceRequired, "Add at least one photo.");
            var assets = await data.ListAssetsAsync(request.ProjectId, false, token);
            foreach (var input in items)
            {
                var description = Text.Trim(input.Description, 500);
                var diagnosis = Text.Trim(input.Diagnosis, 2000);
                if (description is null || diagnosis is null) return OperationResult<bool>.Fail(AfterSalesErrorCodes.ValidationFailed, "Every item needs a description and a diagnosis.");
                if (input.AssetId is { } assetId && assets.All(a => a.Id != assetId)) return OperationResult<bool>.Fail(AfterSalesErrorCodes.AssetNotFound, "Asset was not found.");
                data.Add(NewItem(request.Id, input, description, diagnosis, technicianUserId, supplementary: false, now));
            }
            if (stayOnSite)
            {
                // The diagnosis is recorded, the visit and its reserved interval stay open.
                visit.Report = text;
                AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "TECHNICIAN_WAITING_ON_SITE", technicianUserId,
                    $"diagnosis reported; the technician can repair during this visit until {visit.EndsAt:O}", now);
            }
            else
            {
                CloseVisit(visit, assignment, text, now);
            }
            if (noFaultFound)
            {
                request.Outcome = ServiceOutcomes.NoFaultFound;
                AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.PendingCustomerAcceptance, technicianUserId, "no fault found", now);
                AfterSalesHistory.Notify(data, request.CustomerUserId, "NO_FAULT_FOUND", $"No fault was found for {request.RequestNumber}. Please confirm the result.", now, request.Id);
            }
            else
            {
                AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.AwaitingDecision, technicianUserId, $"{items.Count} item(s) diagnosed", now);
                AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "DIAGNOSIS_SUBMITTED",
                    stayOnSite
                        ? $"{request.RequestNumber}: diagnosis ready; the technician is waiting on site to repair."
                        : $"{request.RequestNumber}: diagnosis ready for a coverage decision.", now, request.Id);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Same-visit repair: the technician who stayed on site continues the inspection visit as the repair visit. Allowed only when every
    /// item is decided and authorized (warranty / protection approved by a Manager, chargeable quotations accepted and paid through
    /// PayOS: the request is READY_FOR_WORK), the parts are available, and the estimated repair fits in the interval already reserved for
    /// this visit (no new booking, so no scheduling conflict). Otherwise the technician finishes the visit and a Manager plans a
    /// separate repair visit.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> ContinueWithRepairAsync(Guid technicianUserId, Guid visitId, int estimatedMinutes, bool partsAvailable,
        CancellationToken cancellationToken)
    {
        if (estimatedMinutes is < 1 or > 480) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "Estimate the repair between 1 and 480 minutes."));
        return MutateAsync(technicianUserId, visitId, (visit, assignment, request, now, _) =>
        {
            if (!VisitStatuses.IsWaitingOnSite(visit) || assignment?.Status != AssignmentStatuses.Accepted)
                return Task.FromResult(Bad("Only an inspection whose diagnosis you reported while staying on site can continue as a repair."));
            if (request.Status != ServiceRequestStatuses.ReadyForWork)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.PaymentRequired,
                    "The work is not authorized yet: a Manager must decide every item and chargeable quotations must be accepted and paid."));
            if (!partsAvailable)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.PartsUnavailable, "Finish the visit; a Manager will plan the repair when the parts are available."));
            if (now.AddMinutes(estimatedMinutes) > visit.EndsAt)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.RepairTimeInsufficient,
                    $"The repair does not fit in this visit (reserved until {visit.EndsAt:O}). Finish the visit; a Manager will plan the repair."));
            visit.Purpose = VisitPurposes.Repair;
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "VISIT_CONTINUED_AS_REPAIR", technicianUserId,
                $"same-visit repair, estimated {estimatedMinutes} minutes", now);
            AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.InProgress, technicianUserId, "repair started during the inspection visit", now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>
    /// Ends an on-site wait without repairing (not authorized in time, parts missing, not enough time). The inspection is kept as
    /// reported; the request continues with the decision / quotation flow and a Manager schedules a separate repair visit.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> FinishOnSiteWaitAsync(Guid technicianUserId, Guid visitId, string? note, CancellationToken cancellationToken)
        => MutateAsync(technicianUserId, visitId, (visit, assignment, request, now, _) =>
        {
            if (!VisitStatuses.IsWaitingOnSite(visit)) return Task.FromResult(Bad("This visit is not waiting on site."));
            CloseOnSiteWait(data, visit, assignment, request, technicianUserId, Text.Trim(note, 500) ?? "finished by the technician", now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>Closes an on-site wait as a normal reported inspection (also used by the sweep when the reserved interval ended).</summary>
    public static void CloseOnSiteWait(IAfterSalesUnitOfWork data, ServiceVisit visit, InstallationAssignment? assignment, ServiceRequest request, Guid? actorUserId,
        string reason, DateTimeOffset now)
    {
        CloseVisit(visit, assignment, visit.Report!, now);
        AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "ON_SITE_WAIT_ENDED", actorUserId, $"{reason}; a separate repair visit is needed", now);
    }

    /// <summary>
    /// A further fault found during a repair. It needs its own coverage decision and, when chargeable, a separate supplementary quotation;
    /// the already authorized work may continue and the paid quotation never changes.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> AddSupplementaryItemAsync(Guid technicianUserId, Guid visitId, DiagnosedItemInput input, CancellationToken cancellationToken)
    {
        var description = Text.Trim(input.Description, 500);
        var diagnosis = Text.Trim(input.Diagnosis, 2000);
        if (description is null || diagnosis is null) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "The item needs a description and a diagnosis."));
        return MutateAsync(technicianUserId, visitId, async (visit, _, request, now, token) =>
        {
            if (visit.Purpose is not (VisitPurposes.Repair or VisitPurposes.Rework) || visit.Status != VisitStatuses.InProgress) return Bad("Additional faults are reported during a repair.");
            data.Add(NewItem(request.Id, input, description, diagnosis, technicianUserId, supplementary: true, now));
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "SUPPLEMENTARY_ITEM_FOUND", technicianUserId, description, now);
            AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "SUPPLEMENTARY_ITEM_FOUND",
                $"{request.RequestNumber}: an additional fault needs a coverage decision.", now, request.Id);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Reports finished repair, rework or maintenance work (server time = completion time). Authorized items reported here become DONE;
    /// replacements are stored as PENDING_ACCEPTANCE assets (serial numbers must not clash). For paid repair work the repair warranty starts
    /// now (months from the paid quotation); a corrective rework report restarts it. Maintenance waits for the customer's acceptance.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> ReportWorkAsync(Guid technicianUserId, Guid visitId, string? report, IReadOnlyList<ItemResultInput> results,
        IReadOnlyList<ReplacementInput> replacements, string? findingNote, CancellationToken cancellationToken)
    {
        var text = Text.Trim(report, 4000);
        if (text is null) return Task.FromResult(Fail(AfterSalesErrorCodes.ValidationFailed, "Describe the work you did."));
        return MutateAsync(technicianUserId, visitId, async (visit, assignment, request, now, token) =>
        {
            if (visit.Purpose == VisitPurposes.Inspection || visit.Status != VisitStatuses.InProgress) return Bad("Start the work first.");
            if (!await HasEvidenceAsync(request.Id, visit.Id, token)) return OperationResult<bool>.Fail(AfterSalesErrorCodes.EvidenceRequired, "Add at least one photo.");
            var items = await data.ListItemsAsync(request.Id, true, token);
            var repairedPaidWork = false;
            foreach (var result in results)
            {
                var item = items.FirstOrDefault(i => i.Id == result.ItemId);
                if (item is null) return OperationResult<bool>.Fail(AfterSalesErrorCodes.ItemNotFound, "Item was not found.");
                if (item.Status is not (ItemStatuses.Authorized or ItemStatuses.Done)) return Bad("Only authorized work can be reported (paid items must be paid first).");
                item.Status = ItemStatuses.Done;
                item.WorkResult = Text.Trim(result.WorkResult, 2000);
                item.PartsUsed = Text.Trim(result.PartsUsed, 1000);
                item.UpdatedAt = now;
                repairedPaidWork |= ItemCoverages.NeedsQuotation(item.Coverage);
            }
            var replacementError = await AddReplacementsAsync(request, replacements, now, token);
            if (replacementError is not null) return replacementError;

            // A same-visit repair keeps the inspection report in front of the work report.
            CloseVisit(visit, assignment, visit.Report is { } diagnosis ? $"{diagnosis}\n\n{text}" : text, now);
            visit.FindingNote = Text.Trim(findingNote, 2000);
            if (request.Kind != ServiceRequestKinds.Fault)
            {
                request.Outcome = ServiceOutcomes.MaintenanceDone;
                AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.PendingCustomerAcceptance, technicianUserId, "maintenance reported", now);
            }
            else
            {
                // Paid repair warranty: from this verified completion report (a corrective rework report restarts it).
                var months = (await data.ListQuotationsAsync(request.Id, false, token)).Where(q => q.Status == QuotationStatuses.Paid).Max(q => q.RepairWarrantyMonths);
                if ((repairedPaidWork || visit.Purpose == VisitPurposes.Rework && request.RepairWarrantyStartsAt is not null) && months is { } m)
                {
                    request.RepairWarrantyMonths = m;
                    request.RepairWarrantyStartsAt = now;
                    request.RepairWarrantyEndsAt = AfterSalesRules.AddMonths(now, m, calendar.TimeZone);
                }
                var next = AfterSalesWorkflow.NextStatus(items);
                if (next == ServiceRequestStatuses.PendingCustomerAcceptance) request.Outcome = ServiceOutcomes.Repaired;
                AfterSalesWorkflow.Move(data, request, next, technicianUserId, "work reported", now);
            }
            if (request.Status == ServiceRequestStatuses.PendingCustomerAcceptance)
                AfterSalesHistory.Notify(data, request.CustomerUserId, "ACCEPTANCE_REQUESTED", $"{request.RequestNumber} is done. Please accept or reject the result.", now, request.Id);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// The customer was absent or the work could not be done. Nothing is charged and no maintenance visit is used: a maintenance booking is
    /// closed as NOT_COMPLETED and the visit can be booked again; other work goes back to the Manager for another appointment.
    /// </summary>
    public Task<OperationResult<TechnicianVisitView>> ReportAbsenceAsync(Guid technicianUserId, Guid visitId, string? note, CancellationToken cancellationToken)
    {
        var text = Text.Trim(note, 1000);
        if (text is null) return Task.FromResult(Fail(AfterSalesErrorCodes.ReasonRequired, "Describe what happened on site."));
        return MutateAsync(technicianUserId, visitId, async (visit, assignment, request, now, token) =>
        {
            if (assignment?.Status != AssignmentStatuses.Accepted || !VisitStatuses.IsOpen(visit.Status)) return Bad("Only an accepted, open visit can be reported.");
            if (now < visit.StartsAt - EarlyStart) return Bad("Report absence on site, at the appointment time.");
            visit.Status = VisitStatuses.CustomerAbsent;
            visit.Report = text;
            visit.ReportedAt = now;
            assignment.Status = AssignmentStatuses.Completed;
            assignment.ClosedAt = now;
            if (request.Kind == ServiceRequestKinds.Maintenance)
            {
                var entitlement = (await data.FindEntitlementAsync(request.MaintenanceEntitlementId!.Value, true, token))!;
                entitlement.Status = EntitlementStatuses.Available;
                entitlement.CurrentRequestId = null;
                entitlement.UpdatedAt = now;
                entitlement.Revision++;
                request.Outcome = ServiceOutcomes.CustomerAbsent;
                AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.NotCompleted, technicianUserId, $"customer absent: {text}", now);
                AfterSalesHistory.Notify(data, request.CustomerUserId, "VISIT_MISSED", "We could not do the maintenance visit. You can book it again.", now, request.Id);
            }
            else
            {
                AfterSalesWorkflow.Move(data, request, StatusBefore(visit, request), technicianUserId, $"customer absent: {text}", now);
                AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "VISIT_MISSED",
                    $"{request.RequestNumber}: the customer was absent; plan another visit.", now, request.Id);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== helpers

    private async Task<OperationResult<bool>?> AddReplacementsAsync(ServiceRequest request, IReadOnlyList<ReplacementInput> replacements, DateTimeOffset now, CancellationToken token)
    {
        foreach (var input in replacements)
        {
            var old = await data.FindAssetAsync(input.ReplacedAssetId, false, token);
            if (old is null || old.ProjectId != request.ProjectId || old.Status != InstalledAssetStatuses.Active)
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.AssetNotFound, "The replaced asset is not an active asset of this installation.");
            var manufacturer = Text.Trim(input.Manufacturer, 100);
            var model = Text.Trim(input.Model, 100);
            var serial = Text.Trim(input.SerialNumber, 100);
            var specifications = Text.Trim(input.Specifications, 500);
            if (manufacturer is null || model is null || serial is null || specifications is null)
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.ValidationFailed, "A replacement needs manufacturer, model, serial number and specifications.");
            if (await data.SerialExistsAsync(manufacturer, serial, null, token))
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.DuplicateSerial, $"Serial {serial} of {manufacturer} is already registered.");
            data.Add(new InstalledAsset
            {
                Id = Guid.NewGuid(), ProjectId = request.ProjectId, AssetType = old.AssetType, ProductId = null, Manufacturer = manufacturer, Model = model,
                SerialNumber = serial, PositionCode = old.PositionCode, Status = InstalledAssetStatuses.PendingAcceptance, ReplacesAssetId = old.Id,
                ServiceRequestId = request.Id, Specifications = specifications, RegisteredByUserId = old.RegisteredByUserId, RegisteredAt = now,
            });
            AfterSalesHistory.Record(data, EventSubjects.Asset, old.Id, "ASSET_REPLACEMENT_RECORDED", null, $"{old.SerialNumber} → {serial} ({request.RequestNumber})", now);
        }
        return null;
    }

    private static ServiceItem NewItem(Guid requestId, DiagnosedItemInput input, string description, string diagnosis, Guid technicianUserId, bool supplementary, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(), RequestId = requestId, AssetId = input.AssetId, Description = description, Diagnosis = diagnosis,
            RequiredParts = Text.Trim(input.RequiredParts, 1000), Coverage = ItemCoverages.Pending, Status = ItemStatuses.Open, IsSupplementary = supplementary,
            ReportedByUserId = technicianUserId, CreatedAt = now, UpdatedAt = now,
        };

    private static void CloseVisit(ServiceVisit visit, InstallationAssignment? assignment, string report, DateTimeOffset now)
    {
        visit.Status = VisitStatuses.Reported;
        visit.Report = report;
        visit.ReportedAt = now;
        visit.UpdatedAt = now;
        if (assignment is null) return;
        assignment.Status = AssignmentStatuses.Completed;
        assignment.ClosedAt = now;
    }

    /// <summary>The status a request waits in for this kind of visit (used when a visit is declined or missed).</summary>
    private static string StatusBefore(ServiceVisit visit, ServiceRequest request) => visit.Purpose switch
    {
        VisitPurposes.Inspection => ServiceRequestStatuses.Submitted,
        VisitPurposes.Rework => ServiceRequestStatuses.ReworkRequired,
        VisitPurposes.Maintenance when request.Kind == ServiceRequestKinds.Maintenance => ServiceRequestStatuses.Submitted,
        _ => ServiceRequestStatuses.ReadyForWork,
    };

    private async Task<bool> HasEvidenceAsync(Guid requestId, Guid visitId, CancellationToken token)
        => (await data.ListEvidenceAsync(requestId, token)).Any(e => e.VisitId == visitId);

    private async Task<OperationResult<TechnicianVisitView>> MutateAsync(Guid technicianUserId, Guid visitId,
        Func<ServiceVisit, InstallationAssignment?, ServiceRequest, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var visit = await data.FindVisitAsync(visitId, true, token);
                if (visit is null || visit.TechnicianUserId != technicianUserId)
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.VisitNotFound, "Visit was not found."), false);
                await data.LockTechniciansAsync([technicianUserId], token);
                var assignment = await data.FindCurrentAssignmentAsync(visitId, true, token);
                var request = (await data.FindRequestAsync(visit.RequestId, true, token))!;
                var now = time.GetUtcNow();
                var outcome = await change(visit, assignment, request, now, token);
                if (!outcome.Succeeded) return (outcome, false);
                visit.UpdatedAt = now;
                await data.SaveChangesAsync(token);
                return (outcome, true);
            }, cancellationToken);
            return result.Succeeded ? await GetAsync(technicianUserId, visitId, cancellationToken) : result.As<TechnicianVisitView>();
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation)
        {
            return Fail(AfterSalesErrorCodes.DuplicateSerial, "A serial number or photo is already registered.");
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request changed meanwhile. Reload and try again.");
        }
    }

    private async Task<TechnicianVisitView> ViewAsync(ServiceVisit visit, CancellationToken token)
    {
        var request = (await data.FindRequestAsync(visit.RequestId, false, token))!;
        var assignment = await data.FindCurrentAssignmentAsync(visit.Id, false, token);
        var zone = calendar.TimeZone;
        return new TechnicianVisitView(visit.Id, request.Id, request.RequestNumber, request.Kind, visit.Purpose, visit.Status, assignment?.Status,
            TimeZoneInfo.ConvertTime(visit.StartsAt, zone), TimeZoneInfo.ConvertTime(visit.EndsAt, zone), visit.CustomerConfirmationRequired, visit.CustomerConfirmedAt,
            await data.GetSiteAddressAsync(request.PropertySiteId, token), request.Description,
            (await data.ListItemsAsync(request.Id, false, token)).Select(AfterSalesViews.Item).ToList(),
            (await data.ListAssetsAsync(request.ProjectId, false, token)).Select(AfterSalesViews.Asset).ToList(),
            VisitStatuses.IsWaitingOnSite(visit),
            (await data.ListEvidenceAsync(request.Id, token))
                .Select(e => new ServiceEvidenceView(e.Id, e.VisitId, e.FileId, e.Caption, TimeZoneInfo.ConvertTime(e.AddedAt, zone))).ToList());
    }

    private static OperationResult<bool> Bad(string message) => OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, message);
    private static OperationResult<TechnicianVisitView> Fail(string code, string message) => OperationResult<TechnicianVisitView>.Fail(code, message);
    private static OperationResult<TechnicianVisitView> NotFound() => Fail(AfterSalesErrorCodes.VisitNotFound, "Visit was not found.");
}
