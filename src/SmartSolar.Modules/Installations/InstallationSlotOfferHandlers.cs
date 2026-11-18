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
/// Technician-led scheduling (2026-11-18). The responsible technician consults the backend's availability for the contract
/// (<see cref="AvailabilityAsync"/>: working-day intervals checked with the same eligibility as a plan), agrees the date with the customer in person
/// and records it (<see cref="ProposeAsync"/>). The customer no longer selects offered slots in the app: publishing offers and the customer's selection
/// were retired; existing offers stay as read-only history and any OPEN legacy offer is withdrawn when the technician records the agreed date.
/// </summary>
public sealed partial class InstallationScheduleHandlers
{
    public const int MaxAvailabilityDays = 31;

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

    /// <summary>Every offer of the contract (history included) for its responsible technician; another technician gets 404.</summary>
    public async Task<OperationResult<IReadOnlyList<InstallationSlotOfferView>>> ListOffersForTechnicianAsync(Guid technicianUserId, Guid contractId,
        CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetScheduleContractAsync(contractId, cancellationToken);
        if (contract is null || contract.SurveyTechnicianUserId != technicianUserId)
            return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Fail(InstallationScheduleErrorCodes.ContractNotFound, "Contract was not found.");
        return OperationResult<IReadOnlyList<InstallationSlotOfferView>>.Ok(await OfferViewsAsync(contract, true, null, cancellationToken));
    }

    // ================================================================== internals

    internal static void Withdraw(InstallationSlotOffer offer, string reason, DateTimeOffset now)
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
                "Installation times can be consulted once the contract exists and is paid (or SIGNED for a contract of the previous flow).");
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
            }
            views.Add(new InstallationSlotOfferView(o.Id, o.ContractId, o.TechnicianUserId, names.GetValueOrDefault(o.TechnicianUserId, ""), o.StartsAt, o.EndsAt,
                o.Status, o.Status == InstallationSlotOfferStatuses.Open && reason is null, reason, o.Note, o.CreatedAt, o.SelectedAt, o.SelectedScheduleId,
                o.WithdrawnAt, forTechnician ? o.WithdrawnReason : null, o.Revision));
        }
        return views;
    }
}
