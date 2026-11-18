using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Modules.Installations;

/// <param name="TechnicianUserId">Only a Manager/Admin may name the technician (exception handling); a technician always plans for themself.</param>
/// <param name="CustomerAgreementNote">How the customer agreed (phone/on site, when). Required.</param>
/// <param name="Reason">Why the date changes; required when a live schedule already exists.</param>
/// <param name="ExpectedRevision">The live schedule's revision when changing it (optional optimistic check).</param>
public sealed record ProposeInstallationScheduleInput(Guid? TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? CustomerAgreementNote,
    string? Reason, int? ExpectedRevision);

/// <summary>
/// One installation schedule. <see cref="IsConfirmed"/> is true only for CONFIRMED (the contract is PAID and the technician's assignment is
/// accepted); a PLANNED schedule must never be shown as confirmed. Times are in the scheduling time zone.
/// </summary>
public sealed record InstallationScheduleView(
    Guid ScheduleId, Guid ContractId, string ContractNumber, string ContractStatus, Guid SurveyRequestId, Guid PropertySiteId, string PropertyName,
    Guid TechnicianUserId, string TechnicianName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, bool IsConfirmed,
    string CustomerAgreementNote, string? ChangeReason, Guid? ProjectId, string? ProjectStatus, DateTimeOffset? ConfirmedAt,
    string? RescheduleReasonCode, string? RescheduleDetail, DateTimeOffset? CustomerChangeRequestedAt, string? CustomerChangeRequestNote,
    DateTimeOffset? CancelledAt, string? CancellationReason, DateTimeOffset CreatedAt, int Revision, bool AwaitingPayment);

/// <summary>A SIGNED / PAID contract the technician surveyed, with its live schedule (null = none recorded yet).</summary>
public sealed record TechnicianScheduleContractView(Guid ContractId, string ContractNumber, string ContractStatus, string PropertyName, string CustomerName,
    InstallationScheduleView? Schedule);

/// <summary>Called after a contract became PAID (PayOS webhook / reconciliation or a staff cash confirmation), outside the payment transaction.</summary>
public interface IContractPaidFollowUp
{
    Task ContractPaidAsync(Guid contractId, CancellationToken cancellationToken);
}

/// <summary>
/// Technician-agreed installation schedules (business 2026-11-17). After the contract is SIGNED the responsible technician (the one who did
/// the on-site survey) records the date agreed with the customer: the interval is validated (future, horizon, length) and the technician's
/// eligibility is checked (active technician, member of the site's zone, working day and hours, no leave, no other commitment within the
/// buffer). The plan is PLANNED and holds no capacity. When the contract is PAID the system creates (or reuses) the contract's single
/// installation project and turns the plan into the technician's assignment through <see cref="InstallationHandlers.AssignAsync"/> (same
/// locks, re-check and exclusion constraint as a Manager assignment) and accepts it on the technician's behalf: CONFIRMED. When that re-check
/// fails, nothing is confirmed and the plan becomes RESCHEDULE_REQUIRED. Work still starts only through
/// <see cref="InstallationHandlers.StartProjectAsync"/> (accepted assignment + EVN / PCCC / structural prerequisites).
/// Manager/Admin keep every project operation (assign, reassign, cancel) for exceptions.
/// </summary>
public sealed partial class InstallationScheduleHandlers(
    IInstallationUnitOfWork unitOfWork,
    ISchedulingUnitOfWork scheduling,
    InstallationSchedulePlanner planner,
    InstallationHandlers installations,
    WorkingCalendar calendar,
    TimeProvider time,
    ILogger<InstallationScheduleHandlers> logger) : IContractPaidFollowUp
{
    private const int MaxNote = 500;

    // ================================================================== Technician (and Manager/Admin exception handling)

    public async Task<IReadOnlyList<TechnicianScheduleContractView>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
    {
        var contracts = await unitOfWork.ListScheduleContractsForTechnicianAsync(technicianUserId, cancellationToken);
        var result = new List<TechnicianScheduleContractView>();
        foreach (var c in contracts)
        {
            var live = await unitOfWork.GetLiveProposalAsync(c.ContractId, false, cancellationToken);
            result.Add(new TechnicianScheduleContractView(c.ContractId, c.ContractNumber, c.Status, c.PropertyName, c.CustomerName,
                live is null ? null : await ViewAsync(live, c, cancellationToken)));
        }
        return result;
    }

    /// <summary>The contract's schedule history (newest first) for its responsible technician; any other technician gets 404.</summary>
    public async Task<OperationResult<IReadOnlyList<InstallationScheduleView>>> GetForTechnicianAsync(Guid technicianUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || !Schedulable(contract) || contract.SurveyTechnicianUserId != technicianUserId)
            return OperationResult<IReadOnlyList<InstallationScheduleView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        return OperationResult<IReadOnlyList<InstallationScheduleView>>.Ok(await HistoryAsync(contract, cancellationToken));
    }

    /// <summary>
    /// Records the agreed date (PLANNED) or changes it (the live row is CANCELLED with the reason and a new PLANNED row is inserted). When the
    /// contract is already PAID the new plan is confirmed immediately.
    /// </summary>
    public async Task<OperationResult<InstallationScheduleView>> ProposeAsync(Guid actorUserId, bool isStaff, Guid contractId, ProposeInstallationScheduleInput input,
        CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || (!isStaff && contract.SurveyTechnicianUserId != actorUserId))
            return Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        if (!Schedulable(contract))
            return Fail(InstallationScheduleErrorCodes.ContractNotSigned, "An installation date can be agreed only after the contract is SIGNED.");
        var technicianId = isStaff ? input.TechnicianUserId ?? contract.SurveyTechnicianUserId : actorUserId;
        if (technicianId is not { } technician)
            return Fail(CommonErrorCodes.ValidationFailed, "'technicianUserId' is required: this contract has no surveying technician.");
        var agreement = Trim(input.CustomerAgreementNote);
        if (agreement is null)
            return Fail(InstallationScheduleErrorCodes.AgreementRequired, "Record how and when the customer agreed to this date ('customerAgreementNote').");
        var startsAt = input.StartsAt.ToUniversalTime();
        var endsAt = input.EndsAt.ToUniversalTime();
        if (await planner.ValidateIntervalAsync(startsAt, endsAt, time.GetUtcNow(), cancellationToken) is { } invalid)
            return Fail(InstallationScheduleErrorCodes.InvalidSchedule, invalid);

        var project = await unitOfWork.FindProjectByContractAsync(contractId, cancellationToken);
        InstallationAssignment? current = null;
        if (project is not null)
        {
            if (InstallationStatuses.IsStarted(project.Status) || project.Status == InstallationStatuses.Cancelled)
                return Fail(InstallationScheduleErrorCodes.InstallationStarted, $"The installation is {project.Status}: its schedule can no longer be changed here.");
            current = await unitOfWork.GetCurrentAssignmentAsync(project.Id, false, cancellationToken);
            if (current is not null && current.TechnicianUserId != technician && !isStaff)
                return Fail(InstallationScheduleErrorCodes.AssignedToOtherTechnician, "A Manager assigned this installation to another technician.");
        }

        var coverage = await scheduling.GetSiteCoverageAsync(contract.PropertySiteId, cancellationToken);
        if (coverage.Status != CoverageStatuses.Covered)
            return Fail(InstallationScheduleErrorCodes.CoverageUnavailable,
                "The site's area is not served by an operational zone right now; a Manager must arrange the installation.", new { coverageStatus = coverage.Status });
        var (name, ineligible, _) = await planner.CheckAsync(technician, startsAt, endsAt, coverage.ZoneIds,
            current is null ? null : new HashSet<Guid> { current.Id }, cancellationToken);
        if (ineligible is not null)
            return Fail(InstallationScheduleErrorCodes.TechnicianIneligible, $"{name} cannot take this interval: {ineligible}.", new { reason = ineligible });

        return await RecordPlanAsync(actorUserId, contract, technician, startsAt, endsAt, agreement, Trim(input.Reason), input.ExpectedRevision, null, null,
            cancellationToken);
    }

    /// <summary>
    /// The locked write shared by the technician / Manager plan and the customer's selection of an offered interval: contract lock, contract still
    /// SIGNED/PAID, optional <paramref name="guard"/> (inside the transaction, after the lock; a failure writes nothing), stale-revision check,
    /// idempotent repeat, replacement of the live row (reason required), new PLANNED row, customer notification; confirmation when PAID.
    /// <paramref name="onRecorded"/> runs in the same transaction with the row that now carries the plan (new, or the identical live one).
    /// </summary>
    private async Task<OperationResult<InstallationScheduleView>> RecordPlanAsync(Guid actorUserId, ScheduleContractInfo contract, Guid technician,
        DateTimeOffset startsAt, DateTimeOffset endsAt, string agreement, string? reason, int? expectedRevision,
        Func<ScheduleContractInfo, CancellationToken, Task<OperationResult<bool>>>? guard, Action<InstallationScheduleProposal, DateTimeOffset>? onRecorded,
        CancellationToken cancellationToken)
    {
        var contractId = contract.ContractId;
        Guid? createdId = null;
        OperationResult<bool> written;
        try
        {
            written = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await unitOfWork.LockContractAsync(contractId, token);
                var fresh = await unitOfWork.GetScheduleContractAsync(contractId, token);
                if (fresh is null || !Schedulable(fresh))
                    return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.ContractNotSigned, "The contract is no longer SIGNED or PAID."), false);
                if (guard is not null && await guard(fresh, token) is { Succeeded: false } refused) return (refused, false);
                var live = await unitOfWork.GetLiveProposalAsync(contractId, true, token);
                if (live is not null && expectedRevision is { } expected && live.Revision != expected)
                    return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.StaleRevision, "The schedule changed meanwhile. Reload and try again.",
                        new { currentRevision = live.Revision }), false);
                if (live is not null && live.TechnicianUserId == technician && live.StartsAt == startsAt && live.EndsAt == endsAt
                    && live.Status != InstallationScheduleStatuses.RescheduleRequired)
                {
                    createdId = live.Id;   // the same plan again: idempotent
                    if (onRecorded is null) return (OperationResult<bool>.Ok(false), false);
                    onRecorded(live, time.GetUtcNow());
                    await unitOfWork.SaveChangesAsync(token);
                    return (OperationResult<bool>.Ok(false), true);
                }
                if (live is not null && reason is null)
                    return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.ReasonRequired, "Give the reason for changing the agreed date ('reason')."), false);
                var now = time.GetUtcNow();
                if (live is not null)
                {
                    live.Status = InstallationScheduleStatuses.Cancelled;
                    live.CancelledAt = now;
                    live.CancelledByUserId = actorUserId;
                    live.CancellationReason = Trim($"REPLACED: {reason}");
                    live.UpdatedAt = now;
                    live.Revision++;
                    // The replaced row leaves the "one live schedule" index before the new row is inserted.
                    await unitOfWork.SaveChangesAsync(token);
                }
                var proposal = new InstallationScheduleProposal
                {
                    Id = Guid.NewGuid(), ContractId = contractId, SurveyRequestId = fresh.SurveyRequestId, CustomerUserId = fresh.CustomerUserId,
                    PropertySiteId = fresh.PropertySiteId, TechnicianUserId = technician, StartsAt = startsAt, EndsAt = endsAt,
                    Status = InstallationScheduleStatuses.Planned, CustomerAgreementNote = agreement, ChangeReason = live is null ? null : reason,
                    ReplacesProposalId = live?.Id, CreatedByUserId = actorUserId, CreatedAt = now, UpdatedAt = now,
                };
                unitOfWork.Add(proposal);
                onRecorded?.Invoke(proposal, now);
                var when = $"{Local(startsAt):dd/MM/yyyy HH:mm} – {Local(endsAt):dd/MM/yyyy HH:mm}";
                unitOfWork.AddNotification(fresh.CustomerUserId,
                    live is null ? CustomerNotificationTypes.InstallationSchedulePlanned : CustomerNotificationTypes.InstallationScheduleChanged,
                    fresh.Status == ContractStatuses.Paid
                        ? $"Lịch thi công dự kiến {when} đã được ghi nhận; hệ thống đang xác nhận với kỹ thuật viên."
                        : $"Lịch thi công dự kiến {when} đã được ghi nhận. Lịch chỉ được xác nhận sau khi hợp đồng được thanh toán.",
                    now);
                await unitOfWork.SaveChangesAsync(token);
                createdId = proposal.Id;
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Fail(InstallationScheduleErrorCodes.StaleRevision, "The schedule changed meanwhile. Reload and try again.");
        }
        if (!written.Succeeded) return written.As<InstallationScheduleView>();

        if (contract.Status == ContractStatuses.Paid || (await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken))?.Status == ContractStatuses.Paid)
            await ConfirmForContractAsync(contractId, cancellationToken);
        var stored = await unitOfWork.FindProposalAsync(createdId!.Value, false, cancellationToken);
        return OperationResult<InstallationScheduleView>.Ok(await ViewAsync(stored!, null, cancellationToken));
    }

    /// <summary>
    /// Cancels a PLANNED or RESCHEDULE_REQUIRED schedule (reason required): its technician or a Manager/Admin. A CONFIRMED schedule is the
    /// project's assignment: a Manager/Admin reassigns or cancels the project instead (or the technician records a new date).
    /// </summary>
    public async Task<OperationResult<InstallationScheduleView>> CancelAsync(Guid actorUserId, bool isStaff, Guid scheduleId, string? reason, CancellationToken cancellationToken)
    {
        var why = Trim(reason);
        if (why is null) return Fail(InstallationScheduleErrorCodes.ReasonRequired, "A reason is required.");
        var existing = await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken);
        if (existing is null || (!isStaff && existing.TechnicianUserId != actorUserId)) return Fail(InstallationScheduleErrorCodes.NotFound, "Schedule was not found.");
        var result = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
        {
            await unitOfWork.LockContractAsync(existing.ContractId, token);
            var p = await unitOfWork.FindProposalAsync(scheduleId, true, token);
            if (p is null || p.Status is InstallationScheduleStatuses.Cancelled or InstallationScheduleStatuses.Confirmed)
                return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.NotLive,
                    "Only a PLANNED or RESCHEDULE_REQUIRED schedule can be cancelled here."), false);
            var now = time.GetUtcNow();
            p.Status = InstallationScheduleStatuses.Cancelled;
            p.CancelledAt = now;
            p.CancelledByUserId = actorUserId;
            p.CancellationReason = why;
            p.UpdatedAt = now;
            p.Revision++;
            unitOfWork.AddNotification(p.CustomerUserId, CustomerNotificationTypes.InstallationScheduleCancelled,
                $"Lịch thi công dự kiến {Local(p.StartsAt):dd/MM/yyyy HH:mm} đã bị hủy: {why}. Kỹ thuật viên sẽ liên hệ để thống nhất ngày mới.", now);
            await unitOfWork.SaveChangesAsync(token);
            return (OperationResult<bool>.Ok(true), true);
        }, cancellationToken);
        if (!result.Succeeded) return result.As<InstallationScheduleView>();
        return OperationResult<InstallationScheduleView>.Ok(await ViewAsync((await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken))!, null, cancellationToken));
    }

    // ================================================================== Customer

    /// <summary>The customer's live schedules (one per contract at most).</summary>
    public async Task<IReadOnlyList<InstallationScheduleView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var views = new List<InstallationScheduleView>();
        foreach (var p in (await unitOfWork.ListProposalsAsync(null, null, customerUserId, null, cancellationToken)).Where(p => InstallationScheduleStatuses.IsLive(p.Status)))
            views.Add(await ViewAsync(p, null, cancellationToken));
        return views;
    }

    /// <summary>The contract's schedule history (newest first) for its customer; another customer gets 404.</summary>
    public async Task<OperationResult<IReadOnlyList<InstallationScheduleView>>> GetForCustomerAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<IReadOnlyList<InstallationScheduleView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        return OperationResult<IReadOnlyList<InstallationScheduleView>>.Ok(await HistoryAsync(contract, cancellationToken));
    }

    /// <summary>The customer asks for another date (note required). The schedule keeps its state; its technician records a new date with a reason.</summary>
    public async Task<OperationResult<InstallationScheduleView>> RequestChangeAsync(Guid customerUserId, Guid scheduleId, string? note, CancellationToken cancellationToken)
    {
        var why = Trim(note);
        if (why is null) return Fail(InstallationScheduleErrorCodes.ReasonRequired, "Tell the technician why and which dates suit you ('note').");
        var existing = await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken);
        if (existing is null || existing.CustomerUserId != customerUserId) return Fail(InstallationScheduleErrorCodes.NotFound, "Schedule was not found.");
        var project = await unitOfWork.FindProjectByContractAsync(existing.ContractId, cancellationToken);
        if (project is not null && InstallationStatuses.IsStarted(project.Status))
            return Fail(InstallationScheduleErrorCodes.InstallationStarted, "The installation has already started.");
        var result = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
        {
            await unitOfWork.LockContractAsync(existing.ContractId, token);
            var p = await unitOfWork.FindProposalAsync(scheduleId, true, token);
            if (p is null || !InstallationScheduleStatuses.IsLive(p.Status))
                return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.NotLive, "This schedule was replaced or cancelled."), false);
            var now = time.GetUtcNow();
            p.CustomerChangeRequestedAt = now;
            p.CustomerChangeRequestNote = why;
            p.UpdatedAt = now;
            p.Revision++;
            await unitOfWork.SaveChangesAsync(token);
            return (OperationResult<bool>.Ok(true), true);
        }, cancellationToken);
        if (!result.Succeeded) return result.As<InstallationScheduleView>();
        return OperationResult<InstallationScheduleView>.Ok(await ViewAsync((await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken))!, null, cancellationToken));
    }

    // ================================================================== Manager / Admin

    /// <summary>One page of schedules for Manager/Admin (SQL paging; newest first).</summary>
    public async Task<Common.Paging.PagedResult<InstallationScheduleView>> PageForStaffAsync(string? status, Common.Paging.PageRequest page, CancellationToken cancellationToken)
    {
        var (rows, total) = await unitOfWork.PageProposalsAsync(status, page.Skip, page.PageSize, cancellationToken);
        var views = new List<InstallationScheduleView>();
        foreach (var p in rows) views.Add(await ViewAsync(p, null, cancellationToken));
        return page.Result(views, total);
    }

    public async Task<IReadOnlyList<InstallationScheduleView>> ListForStaffAsync(string? status, CancellationToken cancellationToken)
    {
        var views = new List<InstallationScheduleView>();
        foreach (var p in await unitOfWork.ListProposalsAsync(null, null, null, status, cancellationToken)) views.Add(await ViewAsync(p, null, cancellationToken));
        return views;
    }

    public async Task<OperationResult<IReadOnlyList<InstallationScheduleView>>> GetForStaffAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        return contract is null
            ? OperationResult<IReadOnlyList<InstallationScheduleView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.")
            : OperationResult<IReadOnlyList<InstallationScheduleView>>.Ok(await HistoryAsync(contract, cancellationToken));
    }

    // ================================================================== System: PAID → confirmation

    public Task ContractPaidAsync(Guid contractId, CancellationToken cancellationToken) => ConfirmForContractAsync(contractId, cancellationToken);

    /// <summary>
    /// Confirms the contract's PLANNED schedule when the contract is PAID: project (created once; unique index), assignment re-checked under
    /// the technician / site / zone locks, accepted on behalf of the technician who agreed the date. Idempotent and safe to run concurrently
    /// (webhook, reconciliation, cash confirmation, sweep, a Manager retry). Returns the live schedule (null = none).
    /// </summary>
    public async Task<OperationResult<InstallationScheduleView?>> ConfirmForContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var plan = await unitOfWork.GetLiveProposalAsync(contractId, false, cancellationToken);
        if (plan is null) return OperationResult<InstallationScheduleView?>.Ok(null);
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (plan.Status != InstallationScheduleStatuses.Planned || contract?.Status != ContractStatuses.Paid)
            return OperationResult<InstallationScheduleView?>.Ok(await ViewAsync(plan, contract, cancellationToken));

        var project = await unitOfWork.FindProjectByContractAsync(contractId, cancellationToken);
        if (project is null)
        {
            var created = await installations.CreateProjectAsync(null, contractId, cancellationToken, actorUserId: plan.TechnicianUserId);
            if (!created.Succeeded && created.ErrorCode != InstallationErrorCodes.ProjectExists)
            {
                logger.LogWarning("Installation project for paid contract {ContractId} not created yet: {Code}.", contractId, created.ErrorCode);
                return created.As<InstallationScheduleView?>();
            }
            project = await unitOfWork.FindProjectByContractAsync(contractId, cancellationToken);
        }
        if (project is null) return OperationResult<InstallationScheduleView?>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await unitOfWork.GetCurrentAssignmentAsync(project.Id, false, cancellationToken);
            var matches = current is not null && current.TechnicianUserId == plan.TechnicianUserId && current.StartsAt == plan.StartsAt && current.EndsAt == plan.EndsAt;
            if (!matches)
            {
                if (!InstallationStatuses.IsAssignable(project.Status) || InstallationStatuses.IsStarted(project.Status))
                    return await RescheduleAsync(plan.Id, InstallationScheduleRescheduleReasons.ProjectNotAssignable, $"The installation is {project.Status}.", cancellationToken);
                if (current is not null && current.TechnicianUserId != plan.TechnicianUserId)
                    return await RescheduleAsync(plan.Id, InstallationScheduleRescheduleReasons.ProjectNotAssignable,
                        "A Manager assigned the installation to another technician.", cancellationToken);
                var assigned = await installations.AssignAsync(plan.TechnicianUserId, project.Id, new AssignTechnicianInput(plan.TechnicianUserId, plan.StartsAt, plan.EndsAt,
                    current is null ? null : $"Ngày thi công đã thống nhất với khách hàng (lịch {plan.Id})", project.Revision, ScheduleProposalId: plan.Id), cancellationToken);
                if (!assigned.Succeeded)
                {
                    // A concurrent confirmation of the same plan may have just created exactly this assignment (its own assignment then looks
                    // like a conflict): re-read and finish the confirmation instead of flagging the plan.
                    var now = await unitOfWork.GetCurrentAssignmentAsync(project.Id, false, cancellationToken);
                    var concurrentlyAssigned = now is not null && now.TechnicianUserId == plan.TechnicianUserId && now.StartsAt == plan.StartsAt && now.EndsAt == plan.EndsAt;
                    if (assigned.ErrorCode == InstallationErrorCodes.StaleRevision || concurrentlyAssigned)
                    {
                        project = (await unitOfWork.FindProjectByContractAsync(contractId, cancellationToken))!;
                        continue;
                    }
                    var code = assigned.ErrorCode switch
                    {
                        InstallationErrorCodes.InvalidSchedule => InstallationScheduleRescheduleReasons.InvalidSchedule,
                        InstallationErrorCodes.CrossZoneAuthorizationRequired or InstallationErrorCodes.InvalidState => InstallationScheduleRescheduleReasons.ProjectNotAssignable,
                        _ when assigned.ErrorCode is not null && CoverageStatuses.All.Contains(assigned.ErrorCode) => InstallationScheduleRescheduleReasons.CoverageChanged,
                        _ => InstallationScheduleRescheduleReasons.TechnicianUnavailable,
                    };
                    return await RescheduleAsync(plan.Id, code, $"{assigned.ErrorCode}: {assigned.Message}", cancellationToken);
                }
            }
            var accepted = await installations.AcceptAssignmentAsync(plan.TechnicianUserId, project.Id, null, cancellationToken);
            if (!accepted.Succeeded && accepted.ErrorCode != InstallationErrorCodes.StaleRevision)
                return await RescheduleAsync(plan.Id, InstallationScheduleRescheduleReasons.InvalidSchedule, $"{accepted.ErrorCode}: {accepted.Message}", cancellationToken);
            if (!accepted.Succeeded) continue;
            return await MarkConfirmedAsync(plan.Id, project.Id, cancellationToken);
        }
        return OperationResult<InstallationScheduleView?>.Fail(InstallationScheduleErrorCodes.StaleRevision, "The installation kept changing; the confirmation is retried later.");
    }

    /// <summary>Background sweep: confirms plans of PAID contracts (missed follow-ups) and flags plans whose start passed before payment.</summary>
    public async Task<(int Confirmed, int Expired)> SweepAsync(int limit, CancellationToken cancellationToken)
    {
        var confirmed = 0;
        foreach (var contractId in await unitOfWork.ListContractsWithPlannedProposalAndPaidAsync(limit, cancellationToken))
            if ((await ConfirmForContractAsync(contractId, cancellationToken)).Value?.Status == InstallationScheduleStatuses.Confirmed) confirmed++;
        var expired = 0;
        foreach (var id in await unitOfWork.ListExpiredPlannedProposalIdsAsync(time.GetUtcNow(), limit, cancellationToken))
            if ((await RescheduleAsync(id, InstallationScheduleRescheduleReasons.InvalidSchedule, "The agreed start passed before the contract was paid.",
                    cancellationToken)).Succeeded) expired++;
        return (confirmed, expired);
    }

    /// <summary>Called by <see cref="InstallationHandlers"/> inside its transaction when the project leaves the plan behind.</summary>
    internal static async Task ReleaseLiveAsync(IInstallationUnitOfWork unitOfWork, Guid contractId, Guid actorUserId, string reason, bool reschedule,
        DateTimeOffset now, CancellationToken token)
    {
        var p = await unitOfWork.GetLiveProposalAsync(contractId, true, token);
        if (p is null) return;
        if (reschedule)
        {
            if (p.Status == InstallationScheduleStatuses.RescheduleRequired) return;
            p.Status = InstallationScheduleStatuses.RescheduleRequired;
            p.RescheduleReasonCode = InstallationScheduleRescheduleReasons.TechnicianUnavailable;
            p.RescheduleDetail = Trim(reason);
            p.RescheduleRequiredAt = now;
            unitOfWork.AddNotification(p.CustomerUserId, CustomerNotificationTypes.InstallationScheduleRescheduleRequired,
                "Lịch thi công đã thống nhất cần được chọn lại; kỹ thuật viên hoặc SmartSolar sẽ liên hệ với bạn.", now);
        }
        else
        {
            p.Status = InstallationScheduleStatuses.Cancelled;
            p.CancelledAt = now;
            p.CancelledByUserId = actorUserId;
            p.CancellationReason = Trim(reason);
        }
        p.UpdatedAt = now;
        p.Revision++;
    }

    // ================================================================== Internals

    private async Task<OperationResult<InstallationScheduleView?>> RescheduleAsync(Guid scheduleId, string code, string detail, CancellationToken cancellationToken)
    {
        var plan = await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken);
        if (plan is null) return OperationResult<InstallationScheduleView?>.Ok(null);
        await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            await unitOfWork.LockContractAsync(plan.ContractId, token);
            var p = await unitOfWork.FindProposalAsync(scheduleId, true, token);
            if (p is null || p.Status != InstallationScheduleStatuses.Planned) return (false, false);
            if (await unitOfWork.FindProjectByContractAsync(p.ContractId, token) is { } project
                && await unitOfWork.GetCurrentAssignmentAsync(project.Id, false, token) is { } current
                && current.TechnicianUserId == p.TechnicianUserId && current.StartsAt == p.StartsAt && current.EndsAt == p.EndsAt)
                return (false, false);   // the plan IS the project's assignment (a concurrent confirmation): never flag it
            var now = time.GetUtcNow();
            p.Status = InstallationScheduleStatuses.RescheduleRequired;
            p.RescheduleReasonCode = code;
            p.RescheduleDetail = Trim(detail);
            p.RescheduleRequiredAt = now;
            p.UpdatedAt = now;
            p.Revision++;
            unitOfWork.AddNotification(p.CustomerUserId, CustomerNotificationTypes.InstallationScheduleRescheduleRequired,
                $"Lịch thi công dự kiến {Local(p.StartsAt):dd/MM/yyyy HH:mm} không thể xác nhận; kỹ thuật viên sẽ liên hệ để chọn lại ngày.", now);
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        logger.LogInformation("Installation schedule {ScheduleId} needs a new date: {Code}.", scheduleId, code);
        var stored = await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken);
        return OperationResult<InstallationScheduleView?>.Ok(stored is null ? null : await ViewAsync(stored, null, cancellationToken));
    }

    private async Task<OperationResult<InstallationScheduleView?>> MarkConfirmedAsync(Guid scheduleId, Guid projectId, CancellationToken cancellationToken)
    {
        var plan = (await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken))!;
        await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            await unitOfWork.LockContractAsync(plan.ContractId, token);
            var p = await unitOfWork.FindProposalAsync(scheduleId, true, token);
            if (p is null || p.Status != InstallationScheduleStatuses.Planned) return (false, false);
            var now = time.GetUtcNow();
            p.Status = InstallationScheduleStatuses.Confirmed;
            p.ProjectId = projectId;
            p.ConfirmedAt = now;
            p.UpdatedAt = now;
            p.Revision++;
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        var stored = await unitOfWork.FindProposalAsync(scheduleId, false, cancellationToken);
        return OperationResult<InstallationScheduleView?>.Ok(await ViewAsync(stored!, null, cancellationToken));
    }

    private async Task<IReadOnlyList<InstallationScheduleView>> HistoryAsync(ScheduleContractInfo contract, CancellationToken cancellationToken)
    {
        var views = new List<InstallationScheduleView>();
        foreach (var p in await unitOfWork.ListProposalsAsync(contract.ContractId, null, null, null, cancellationToken)) views.Add(await ViewAsync(p, contract, cancellationToken));
        return views;
    }

    private async Task<InstallationScheduleView> ViewAsync(InstallationScheduleProposal p, ScheduleContractInfo? contract, CancellationToken cancellationToken)
    {
        contract ??= await unitOfWork.GetScheduleContractAsync(p.ContractId, cancellationToken);
        var names = await unitOfWork.GetUserNamesAsync([p.TechnicianUserId], cancellationToken);
        var project = await unitOfWork.FindProjectByContractAsync(p.ContractId, cancellationToken);
        return new InstallationScheduleView(p.Id, p.ContractId, contract?.ContractNumber ?? "", contract?.Status ?? "", p.SurveyRequestId, p.PropertySiteId,
            contract?.PropertyName ?? "", p.TechnicianUserId, names.GetValueOrDefault(p.TechnicianUserId) ?? "", Local(p.StartsAt), Local(p.EndsAt), p.Status,
            p.Status == InstallationScheduleStatuses.Confirmed, p.CustomerAgreementNote, p.ChangeReason, p.ProjectId ?? project?.Id, project?.Status,
            p.ConfirmedAt is { } c ? Local(c) : null, p.RescheduleReasonCode, p.RescheduleDetail,
            p.CustomerChangeRequestedAt is { } r ? Local(r) : null, p.CustomerChangeRequestNote, p.CancelledAt is { } x ? Local(x) : null, p.CancellationReason,
            Local(p.CreatedAt), p.Revision,
            AwaitingPayment: p.Status == InstallationScheduleStatuses.Planned && contract?.Status != ContractStatuses.Paid);
    }

    private static bool Schedulable(ScheduleContractInfo c) => c.Status is ContractStatuses.Signed or ContractStatuses.Paid;
    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, calendar.TimeZone);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= MaxNote ? value.Trim() : value.Trim()[..MaxNote];
    private static OperationResult<InstallationScheduleView> Fail(string code, string message, object? details = null)
        => OperationResult<InstallationScheduleView>.Fail(code, message, details);
}
