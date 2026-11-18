using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Modules.Installations;

/// <summary>One candidate start day for the technician: the interval it would occupy and whether it is possible now.</summary>
/// <param name="Reason">Why it is not possible (null when available): the same eligibility reasons as recording a plan.</param>
public sealed record InstallationSlotAvailabilityView(DateOnly Date, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Available, string? Reason);

/// <summary>An offered interval. <see cref="Available"/> is re-evaluated at every read (an offer holds no capacity).</summary>
/// <param name="UnavailableReason">For the technician the precise reason; for the customer a generic message (no other customer's data).</param>
public sealed record InstallationSlotOfferView(Guid OfferId, Guid ContractId, Guid TechnicianUserId, string TechnicianName, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string Status, bool Available, string? UnavailableReason, string? Note, DateTimeOffset CreatedAt, DateTimeOffset? SelectedAt,
    Guid? SelectedScheduleId, DateTimeOffset? WithdrawnAt, string? WithdrawnReason, int Revision);

public sealed record SlotInterval(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// INSTALL-SCHEDULE customer choice (2026-11-18). After the contract is SIGNED (or PAID) the responsible technician consults the backend's
/// availability for the contract (<see cref="AvailabilityAsync"/>: working-day intervals checked with the same eligibility as a plan) and offers
/// 1–5 intervals (<see cref="PublishOffersAsync"/>). The customer sees the open offers, each re-checked at read time, and selects one
/// (<see cref="SelectOfferAsync"/>): under the contract lock and the technician lock the offer must still be OPEN, still valid, the technician still
/// eligible and not planned for another customer in that interval; then the normal PLANNED schedule is recorded (agreement = the customer's
/// selection in the portal), the offer becomes SELECTED and the other open offers are withdrawn, in one transaction. Two customers selecting
/// the same technician interval: the technician lock serialises them and the second one is refused (409 INSTALLATION_SLOT_OFFER_UNAVAILABLE).
/// Payment confirmation, rescheduling and the start prerequisites are those of <see cref="InstallationScheduleHandlers"/>, unchanged.
/// </summary>
public sealed partial class InstallationScheduleHandlers
{
    public const int MaxOffers = 5;
    public const int MaxAvailabilityDays = 31;
    private const string CustomerUnavailable = "This time is no longer available. Please choose another one or ask the technician for new dates.";

    // ================================================================== Technician

    /// <summary>
    /// Candidate intervals for <paramref name="durationDays"/> consecutive local days starting on each date of [<paramref name="from"/>,
    /// <paramref name="to"/>] (at most 31 dates): from the first working period of the start day to the end of the last working period of the end
    /// day. Each is checked like a plan (future, horizon, length, technician eligibility, no commitment) and against the technician's live plans
    /// for other contracts. Nothing is written.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<InstallationSlotAvailabilityView>>> AvailabilityAsync(Guid technicianUserId, Guid contractId, DateOnly from,
        DateOnly to, int durationDays, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxAvailabilityDays) errors["to"] = [$"Give a range of 1 to {MaxAvailabilityDays} days ('from' ≤ 'to')."];
        if (durationDays is < 1 or > 60) errors["durationDays"] = ["'durationDays' must be between 1 and 60."];
        if (errors.Count > 0) return OperationResult<IReadOnlyList<InstallationSlotAvailabilityView>>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

        var target = await TargetForTechnicianAsync(technicianUserId, contractId, cancellationToken);
        if (!target.Succeeded) return target.As<IReadOnlyList<InstallationSlotAvailabilityView>>();
        var (contract, zoneIds, ignore) = target.Value!;
        var views = new List<InstallationSlotAvailabilityView>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (Interval(day, durationDays) is not { } interval) continue;   // not a working start / end day
            var reason = await UnavailabilityAsync(contract, technicianUserId, interval, zoneIds, ignore, cancellationToken);
            views.Add(new InstallationSlotAvailabilityView(day, interval.StartsAt, interval.EndsAt, reason is null, reason));
        }
        return OperationResult<IReadOnlyList<InstallationSlotAvailabilityView>>.Ok(views);
    }

    /// <summary>
    /// Publishes 1–5 intervals for the customer to choose (each must be possible now). The previous open offers of the contract are withdrawn
    /// (history kept) and the customer is notified. Allowed while the installation has not started; it never changes the live schedule.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<InstallationSlotOfferView>>> PublishOffersAsync(Guid technicianUserId, Guid contractId,
        IReadOnlyList<SlotInterval> slots, string? note, CancellationToken cancellationToken)
    {
        var normalized = slots.Select(s => new SlotInterval(s.StartsAt.ToUniversalTime(), s.EndsAt.ToUniversalTime())).ToList();
        if (normalized.Count is 0 or > MaxOffers || normalized.Distinct().Count() != normalized.Count)
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["slots"] = [$"Offer between 1 and {MaxOffers} different intervals."] });
        if (note is { Length: > MaxNote })
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["note"] = [$"At most {MaxNote} characters."] });

        var target = await TargetForTechnicianAsync(technicianUserId, contractId, cancellationToken);
        if (!target.Succeeded) return target.As<IReadOnlyList<InstallationSlotOfferView>>();
        var (contract, zoneIds, ignore) = target.Value!;
        foreach (var slot in normalized)
        {
            if (await UnavailabilityAsync(contract, technicianUserId, slot, zoneIds, ignore, cancellationToken) is { } reason)
                return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(InstallationScheduleErrorCodes.OfferUnavailable,
                    $"{Local(slot.StartsAt):dd/MM/yyyy HH:mm} – {Local(slot.EndsAt):dd/MM/yyyy HH:mm} is not possible: {reason}.",
                    new { startsAt = slot.StartsAt, endsAt = slot.EndsAt, reason });
        }

        try
        {
            var written = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await unitOfWork.LockContractAsync(contractId, token);
                var fresh = await unitOfWork.GetScheduleContractAsync(contractId, token);
                if (fresh is null || !Schedulable(fresh))
                    return (OperationResult<bool>.Fail(InstallationScheduleErrorCodes.ContractNotSigned, "The contract is no longer SIGNED or PAID."), false);
                var now = time.GetUtcNow();
                foreach (var open in await unitOfWork.ListSlotOffersAsync(contractId, InstallationSlotOfferStatuses.Open, true, token))
                    Withdraw(open, "REPLACED_BY_NEW_OFFERS", now);
                // The withdrawn rows leave the "one open interval" index before the new ones are inserted.
                await unitOfWork.SaveChangesAsync(token);
                foreach (var slot in normalized.OrderBy(s => s.StartsAt))
                {
                    unitOfWork.Add(new InstallationSlotOffer
                    {
                        Id = Guid.NewGuid(), ContractId = contractId, TechnicianUserId = technicianUserId, StartsAt = slot.StartsAt, EndsAt = slot.EndsAt,
                        Status = InstallationSlotOfferStatuses.Open, Note = Trim(note), CreatedByUserId = technicianUserId, CreatedAt = now, UpdatedAt = now,
                    });
                }
                unitOfWork.AddNotification(fresh.CustomerUserId, CustomerNotificationTypes.InstallationSlotsOffered,
                    $"Kỹ thuật viên đã đề xuất {normalized.Count} khung thời gian thi công cho hợp đồng {fresh.ContractNumber}. Vui lòng chọn một khung phù hợp.", now);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!written.Succeeded) return written.As<IReadOnlyList<InstallationSlotOfferView>>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(InstallationScheduleErrorCodes.StaleRevision,
                "The offers changed meanwhile. Reload and try again.");
        }
        return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Ok(await OfferViewsAsync(contract, true, InstallationSlotOfferStatuses.Open, cancellationToken));
    }

    /// <summary>Every offer of the contract (history included) for its responsible technician; another technician gets 404.</summary>
    public async Task<OperationResult<IReadOnlyList<InstallationSlotOfferView>>> ListOffersForTechnicianAsync(Guid technicianUserId, Guid contractId,
        CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || contract.SurveyTechnicianUserId != technicianUserId)
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Ok(await OfferViewsAsync(contract, true, null, cancellationToken));
    }

    // ================================================================== Customer

    /// <summary>The OPEN offers of the customer's own contract, each re-checked now (another customer's contract: 404).</summary>
    public async Task<OperationResult<IReadOnlyList<InstallationSlotOfferView>>> ListOffersForCustomerAsync(Guid customerUserId, Guid contractId,
        CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Ok(await OfferViewsAsync(contract, false, InstallationSlotOfferStatuses.Open, cancellationToken));
    }

    /// <summary>
    /// The customer selects one OPEN offer: re-validated under the contract and technician locks, then recorded as the PLANNED schedule (confirmed at
    /// once when the contract is already PAID). Repeating the selection of the same offer returns its schedule (idempotent).
    /// </summary>
    public async Task<OperationResult<InstallationScheduleView>> SelectOfferAsync(Guid customerUserId, Guid offerId, CancellationToken cancellationToken)
    {
        var offer = await unitOfWork.FindSlotOfferAsync(offerId, false, cancellationToken);
        var contract = offer is null ? null : await unitOfWork.GetScheduleContractAsync(offer.ContractId, cancellationToken);
        if (offer is null || contract is null || contract.CustomerUserId != customerUserId)
            return Fail(InstallationScheduleErrorCodes.OfferNotFound, "The offered time was not found.");
        if (offer.Status == InstallationSlotOfferStatuses.Selected && offer.SelectedScheduleId is { } chosen
            && await unitOfWork.FindProposalAsync(chosen, false, cancellationToken) is { } schedule && InstallationScheduleStatuses.IsLive(schedule.Status))
            return OperationResult<InstallationScheduleView>.Ok(await ViewAsync(schedule, null, cancellationToken));
        if (offer.Status != InstallationSlotOfferStatuses.Open)
            return Fail(InstallationScheduleErrorCodes.OfferNotOpen, "This offered time was replaced or already chosen. Reload the offers.");
        if (!Schedulable(contract))
            return Fail(InstallationScheduleErrorCodes.ContractNotSigned, "An installation date can be chosen only after the contract is SIGNED.");

        var target = await TargetAsync(contract, offer.TechnicianUserId, cancellationToken);
        if (!target.Succeeded) return target.As<InstallationScheduleView>();
        var (zoneIds, ignore) = target.Value!;
        var interval = new SlotInterval(offer.StartsAt, offer.EndsAt);
        if (await UnavailabilityAsync(contract, offer.TechnicianUserId, interval, zoneIds, ignore, cancellationToken) is not null)
            return Fail(InstallationScheduleErrorCodes.OfferUnavailable, CustomerUnavailable);

        InstallationSlotOffer? tracked = null;
        IReadOnlyList<InstallationSlotOffer> siblings = [];
        return await RecordPlanAsync(customerUserId, contract, offer.TechnicianUserId, offer.StartsAt, offer.EndsAt,
            $"Khách hàng tự chọn khung thời gian kỹ thuật viên đề xuất trên cổng khách hàng (đề xuất {offer.Id}).",
            "Customer selected an offered installation time", null,
            guard: async (_, token) =>
            {
                // Serialises two customers choosing the same technician interval (same lock as every booking/assignment of the technician).
                await unitOfWork.LockTechniciansAsync([offer.TechnicianUserId], token);
                tracked = await unitOfWork.FindSlotOfferAsync(offerId, true, token);
                if (tracked?.Status != InstallationSlotOfferStatuses.Open)
                    return OperationResult<bool>.Fail(InstallationScheduleErrorCodes.OfferNotOpen, "This offered time was replaced or already chosen. Reload the offers.");
                if ((await unitOfWork.ListOverlappingLiveProposalsAsync(offer.TechnicianUserId, offer.ContractId, offer.StartsAt, offer.EndsAt, token)).Count > 0)
                    return OperationResult<bool>.Fail(InstallationScheduleErrorCodes.OfferUnavailable, CustomerUnavailable);
                siblings = (await unitOfWork.ListSlotOffersAsync(offer.ContractId, InstallationSlotOfferStatuses.Open, true, token)).Where(o => o.Id != offerId).ToList();
                return OperationResult<bool>.Ok(true);
            },
            onRecorded: (proposal, now) =>
            {
                tracked!.Status = InstallationSlotOfferStatuses.Selected;
                tracked.SelectedAt = now;
                tracked.SelectedScheduleId = proposal.Id;
                tracked.UpdatedAt = now;
                tracked.Revision++;
                foreach (var other in siblings) Withdraw(other, "ANOTHER_OFFER_SELECTED", now);
            },
            cancellationToken);
    }

    // ================================================================== internals

    private static void Withdraw(InstallationSlotOffer offer, string reason, DateTimeOffset now)
    {
        offer.Status = InstallationSlotOfferStatuses.Withdrawn;
        offer.WithdrawnAt = now;
        offer.WithdrawnReason = reason;
        offer.UpdatedAt = now;
        offer.Revision++;
    }

    /// <summary>[first working period start of <paramref name="day"/>, last working period end of day + duration − 1]; null on a non-working start or end day.</summary>
    private SlotInterval? Interval(DateOnly day, int durationDays)
    {
        var first = calendar.DefaultPeriodsFor(day);
        var lastDay = day.AddDays(durationDays - 1);
        var last = calendar.DefaultPeriodsFor(lastDay);
        if (first.Count == 0 || last.Count == 0) return null;
        return new SlotInterval(calendar.ToUtc(day, first[0].Start), calendar.ToUtc(lastDay, last[^1].End));
    }

    /// <summary>The surveying technician's contract, its site zones and the project's current assignment to ignore (it is being replaced).</summary>
    private async Task<OperationResult<(ScheduleContractInfo Contract, IReadOnlyCollection<Guid> Zones, IReadOnlySet<Guid>? Ignore)>> TargetForTechnicianAsync(
        Guid technicianUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || contract.SurveyTechnicianUserId != technicianUserId)
            return OperationResult<(ScheduleContractInfo, IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        if (!Schedulable(contract))
            return OperationResult<(ScheduleContractInfo, IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Fail(InstallationScheduleErrorCodes.ContractNotSigned,
                "Installation times can be offered only after the contract is SIGNED.");
        var target = await TargetAsync(contract, technicianUserId, cancellationToken);
        return target.Succeeded
            ? OperationResult<(ScheduleContractInfo, IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Ok((contract, target.Value!.Zones, target.Value.Ignore))
            : target.As<(ScheduleContractInfo, IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>();
    }

    private async Task<OperationResult<(IReadOnlyCollection<Guid> Zones, IReadOnlySet<Guid>? Ignore)>> TargetAsync(ScheduleContractInfo contract, Guid technicianUserId,
        CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectByContractAsync(contract.ContractId, cancellationToken);
        IReadOnlySet<Guid>? ignore = null;
        if (project is not null)
        {
            if (InstallationStatuses.IsStarted(project.Status) || project.Status == InstallationStatuses.Cancelled)
                return OperationResult<(IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Fail(InstallationScheduleErrorCodes.InstallationStarted,
                    $"The installation is {project.Status}: its schedule can no longer be changed here.");
            var current = await unitOfWork.GetCurrentAssignmentAsync(project.Id, false, cancellationToken);
            if (current is not null && current.TechnicianUserId != technicianUserId)
                return OperationResult<(IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Fail(InstallationScheduleErrorCodes.AssignedToOtherTechnician,
                    "A Manager assigned this installation to another technician.");
            if (current is not null) ignore = new HashSet<Guid> { current.Id };
        }
        var coverage = await scheduling.GetSiteCoverageAsync(contract.PropertySiteId, cancellationToken);
        if (coverage.Status != CoverageStatuses.Covered)
            return OperationResult<(IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Fail(InstallationScheduleErrorCodes.CoverageUnavailable,
                "The site's area is not served by an operational zone right now; a Manager must arrange the installation.", new { coverageStatus = coverage.Status });
        return OperationResult<(IReadOnlyCollection<Guid>, IReadOnlySet<Guid>?)>.Ok((coverage.ZoneIds, ignore));
    }

    /// <summary>Null when the technician can take the interval for this contract now; otherwise the reason.</summary>
    private async Task<string?> UnavailabilityAsync(ScheduleContractInfo contract, Guid technicianUserId, SlotInterval interval, IReadOnlyCollection<Guid> zoneIds,
        IReadOnlySet<Guid>? ignore, CancellationToken cancellationToken)
    {
        if (await planner.ValidateIntervalAsync(interval.StartsAt, interval.EndsAt, time.GetUtcNow(), cancellationToken) is { } invalid) return invalid;
        var (_, ineligible, _) = await planner.CheckAsync(technicianUserId, interval.StartsAt, interval.EndsAt, zoneIds, ignore, cancellationToken);
        if (ineligible is not null) return ineligible;
        return (await unitOfWork.ListOverlappingLiveProposalsAsync(technicianUserId, contract.ContractId, interval.StartsAt, interval.EndsAt, cancellationToken)).Count > 0
            ? "The technician already has an installation planned in this interval"
            : null;
    }

    private async Task<IReadOnlyList<InstallationSlotOfferView>> OfferViewsAsync(ScheduleContractInfo contract, bool forTechnician, string? status,
        CancellationToken cancellationToken)
    {
        var offers = await unitOfWork.ListSlotOffersAsync(contract.ContractId, status, false, cancellationToken);
        var names = await unitOfWork.GetUserNamesAsync(offers.Select(o => o.TechnicianUserId).Distinct().ToList(), cancellationToken);
        var views = new List<InstallationSlotOfferView>();
        foreach (var o in offers)
        {
            string? reason = null;
            if (o.Status == InstallationSlotOfferStatuses.Open)
            {
                var target = await TargetAsync(contract, o.TechnicianUserId, cancellationToken);
                reason = !target.Succeeded ? target.Message
                    : await UnavailabilityAsync(contract, o.TechnicianUserId, new SlotInterval(o.StartsAt, o.EndsAt), target.Value!.Zones, target.Value.Ignore, cancellationToken);
                if (reason is not null && !forTechnician) reason = CustomerUnavailable;
            }
            views.Add(new InstallationSlotOfferView(o.Id, o.ContractId, o.TechnicianUserId, names.GetValueOrDefault(o.TechnicianUserId, ""), o.StartsAt, o.EndsAt,
                o.Status, o.Status == InstallationSlotOfferStatuses.Open && reason is null, reason, o.Note, o.CreatedAt, o.SelectedAt, o.SelectedScheduleId,
                o.WithdrawnAt, forTechnician ? o.WithdrawnReason : null, o.Revision));
        }
        return views;
    }
}
