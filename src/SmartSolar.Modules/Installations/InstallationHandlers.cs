using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Installations;

public sealed record MilestoneInput(Guid? MilestoneId, string Name, string? Description, int WeightPercent);
public sealed record AssetInput(string AssetType, Guid? ProductId, string Manufacturer, string Model, string SerialNumber, string? PositionCode, int? WarrantyMonths);
public sealed record ChecklistUpdate(bool IsRequired, string Status, Guid? EvidenceFileId, string? Note);

public sealed class InstallationOptions
{
    public const string SectionName = "Installation";
    public int MaxTeamSize { get; set; } = 12;
    public int MaxShifts { get; set; } = 120;
    public int MaxPlanningHorizonDays { get; set; } = 365;
    /// <summary>Acceptance can be requested only after at least one panel and one inverter are registered.</summary>
    public bool RequirePanelAndInverterAssets { get; set; } = true;
}

/// <summary>
/// Installation project use cases. Managers plan (team with exactly one leader, shared daily shifts
/// booked atomically through the scheduling core, milestones, checklist); the leader executes
/// (milestones with verified photo evidence, assets, acceptance request); the customer accepts or
/// requests rework. All transitions are conditional and audited.
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
    // ================================================================== Manager

    public Task<IReadOnlyList<PaidContractSource>> ListReadyContractsAsync(CancellationToken cancellationToken)
        => unitOfWork.ListPaidContractsWithoutProjectAsync(cancellationToken);

    public async Task<OperationResult<InstallationProjectView>> CreateProjectAsync(Guid managerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null) return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ProjectNotFound, "Contract was not found.");
        if (contract.Status != "PAID")
            return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ContractNotPaid, "Installation starts only after the full payment is verified.");

        var now = time.GetUtcNow();
        var project = new InstallationProject
        {
            Id = Guid.NewGuid(), ContractId = contractId, SurveyRequestId = contract.SurveyRequestId, CustomerUserId = contract.CustomerUserId,
            PropertySiteId = contract.PropertySiteId, ManagerUserId = managerUserId, Status = InstallationStatuses.Planning, CreatedAt = now, UpdatedAt = now,
        };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.Add(project);
                var order = 0;
                foreach (var (code, name, weight, mandatory) in DefaultInstallationPlan.Milestones)
                    unitOfWork.Add(new InstallationMilestone
                    {
                        Id = Guid.NewGuid(), ProjectId = project.Id, SortOrder = ++order, Code = code, Name = name, WeightPercent = weight, IsMandatory = mandatory,
                        Status = MilestoneStatuses.NotStarted,
                    });
                foreach (var (code, title) in DefaultInstallationPlan.Checklist)
                    unitOfWork.Add(new InstallationChecklistItem { Id = Guid.NewGuid(), ProjectId = project.Id, Code = code, Title = title, IsRequired = false, Status = ChecklistStatuses.Pending });
                Event(project.Id, "PROJECT_CREATED", managerUserId, $"contract {contract.ContractNumber}", now);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ProjectExists, "An installation project already exists for this contract.");
        }
        return OperationResult<InstallationProjectView>.Ok(await ViewAsync(project.Id, staff: true, cancellationToken));
    }

    public async Task<IReadOnlyList<InstallationProjectView>> ListProjectsAsync(string? status, CancellationToken cancellationToken)
    {
        var list = new List<InstallationProjectView>();
        foreach (var p in await unitOfWork.ListProjectsAsync(status, cancellationToken)) list.Add(await ViewAsync(p.Id, staff: false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<InstallationProjectView>> GetForManagerAsync(Guid projectId, CancellationToken cancellationToken)
        => await unitOfWork.FindProjectAsync(projectId, false, cancellationToken) is null
            ? NotFound()
            : OperationResult<InstallationProjectView>.Ok(await ViewAsync(projectId, staff: true, cancellationToken));

    /// <summary>Shows, per technician and shift, whether the plan fits (no booking is made).</summary>
    public async Task<OperationResult<SchedulePreviewView>> PreviewScheduleAsync(Guid projectId, IReadOnlyList<Guid> members, Guid leaderId, IReadOnlyList<ShiftInput> shifts, CancellationToken cancellationToken)
    {
        var context = await PlanningContextAsync(projectId, members, leaderId, shifts, cancellationToken);
        if (!context.Succeeded) return context.As<SchedulePreviewView>();
        var (project, site, planned, ownAssignments) = context.Value!;
        var plan = await planner.EvaluateAsync(members.Distinct().ToList(), planned, site, ownAssignments, cancellationToken);
        return OperationResult<SchedulePreviewView>.Ok(Preview(plan));
    }

    /// <summary>
    /// Books the team on all shifts atomically: every technician × shift is verified (travel outside
    /// any transaction), then re-checked under the technician locks; future shifts of the project are
    /// replaced, past ones kept. Either everybody is booked or nobody is.
    /// </summary>
    public async Task<OperationResult<InstallationProjectView>> ConfirmScheduleAsync(Guid managerUserId, Guid projectId, int revision,
        IReadOnlyList<Guid> members, Guid leaderId, IReadOnlyList<ShiftInput> shifts, CancellationToken cancellationToken)
    {
        var team = members.Distinct().ToList();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var context = await PlanningContextAsync(projectId, team, leaderId, shifts, cancellationToken);
            if (!context.Succeeded) return context.As<InstallationProjectView>();
            var (_, site, planned, ownAssignments) = context.Value!;
            var plan = await planner.EvaluateAsync(team, planned, site, ownAssignments, cancellationToken);
            if (!plan.IsFeasible)
                return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ScheduleConflict, "Some technicians cannot work some shifts.", Preview(plan));

            OperationResult<bool> outcome;
            try
            {
                outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
                {
                    var now = time.GetUtcNow();
                    var current = await unitOfWork.GetAssignmentsAsync(projectId, activeOnly: true, track: true, token);
                    await unitOfWork.LockTechniciansAsync(team.Concat(current.Select(a => a.TechnicianUserId)), token);
                    var project = await unitOfWork.FindProjectAsync(projectId, true, token);
                    if (project is null) return (Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found."), false);
                    if (project.Revision != revision) return (Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again."), false);
                    if (project.Status is InstallationStatuses.PendingCustomerAcceptance or InstallationStatuses.Completed)
                        return (Fail(InstallationErrorCodes.InvalidState, "The schedule can no longer be changed."), false);
                    var ownNow = current.Where(a => a.StartsAt > now).Select(a => a.Id).ToHashSet();
                    if (!ownNow.SetEquals(ownAssignments) || !await planner.RecheckAsync(plan, site, ownNow, token))
                        return (Fail("RETRY", "schedule changed"), false);

                    // Release this project's future bookings, then book the new plan.
                    var affected = new HashSet<(Guid, DateOnly)>();
                    foreach (var a in current.Where(a => a.StartsAt > now))
                    {
                        a.Status = "RELEASED";
                        a.ReleasedAt = now;
                        affected.Add((a.TechnicianUserId, calendar.LocalDateOf(a.StartsAt)));
                    }
                    foreach (var s in (await unitOfWork.GetShiftsAsync(projectId, true, token)).Where(s => s.Status == "ACTIVE" && s.StartsAt > now))
                        s.Status = "CANCELLED";
                    await unitOfWork.SaveChangesAsync(token);   // free the exclusion constraint before inserting

                    foreach (var shift in planned)
                    {
                        unitOfWork.Add(new InstallationShift
                        {
                            Id = shift.Key, ProjectId = projectId, LocalDate = shift.Date, LocalStart = shift.LocalStart, LocalEnd = shift.LocalEnd,
                            StartsAt = shift.StartsAtUtc, EndsAt = shift.EndsAtUtc, Status = "ACTIVE", CreatedAt = now,
                        });
                        foreach (var technician in team)
                        {
                            unitOfWork.Add(new InstallationShiftAssignment
                            {
                                Id = Guid.NewGuid(), ShiftId = shift.Key, ProjectId = projectId, TechnicianUserId = technician, StartsAt = shift.StartsAtUtc,
                                EndsAt = shift.EndsAtUtc, Status = "ACTIVE", CreatedAt = now,
                            });
                            affected.Add((technician, shift.Date));
                        }
                    }

                    await ReplaceTeamAsync(projectId, team, leaderId, managerUserId, now, token);
                    project.PlannedStartDate = planned.Min(s => s.Date);
                    project.PlannedEndDate = planned.Max(s => s.Date);
                    if (project.Status == InstallationStatuses.Planning) project.Status = InstallationStatuses.Scheduled;
                    project.UpdatedAt = now;
                    project.Revision++;
                    // Survey route chains of the affected technician-days must be re-estimated.
                    foreach (var (technician, date) in affected)
                        await scheduling.RequestRecalculationAsync(technician, date, "INSTALLATION_SCHEDULE_CHANGED", now, token);
                    Event(projectId, "SCHEDULE_CONFIRMED", managerUserId, $"{team.Count} technicians, {planned.Count} shifts, leader {leaderId}", now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (OperationResult<bool>.Ok(true), true);
                }, cancellationToken);
            }
            catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.ExclusionViolation)
            {
                continue;   // a concurrent booking took one of the slots: re-plan
            }
            catch (PersistenceConflictException)
            {
                return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
            }
            if (outcome.ErrorCode == "RETRY") continue;
            if (!outcome.Succeeded) return outcome.As<InstallationProjectView>();
            return OperationResult<InstallationProjectView>.Ok(await ViewAsync(projectId, staff: true, cancellationToken));
        }
        return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ScheduleConflict, "The technicians' schedules kept changing. Preview and try again.");
    }

    public Task<OperationResult<InstallationProjectView>> ChangeLeaderAsync(Guid managerUserId, Guid projectId, int revision, Guid leaderId, CancellationToken cancellationToken)
        => MutateProjectAsync(projectId, revision, async (project, now, token) =>
        {
            if (project.Status == InstallationStatuses.Completed) return Fail(InstallationErrorCodes.InvalidState, "Completed projects are final.");
            var team = await unitOfWork.GetTeamAsync(projectId, true, true, token);
            if (team.All(m => m.TechnicianUserId != leaderId)) return Fail(InstallationErrorCodes.InvalidTeam, "The leader must be a member of the team.");
            foreach (var member in team.Where(m => m.IsLeader && m.TechnicianUserId != leaderId)) member.IsLeader = false;
            await unitOfWork.SaveChangesAsync(token);   // the one-leader index is checked per statement
            team.Single(m => m.TechnicianUserId == leaderId).IsLeader = true;
            Event(projectId, "LEADER_CHANGED", managerUserId, leaderId.ToString(), now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>
    /// Replaces the milestone plan. Before start: free edits (mandatory steps cannot be removed; weights
    /// must total 100). After start: a revision with a reason — completed and in-progress milestones are kept as they are.
    /// </summary>
    public Task<OperationResult<InstallationProjectView>> ReplaceMilestonesAsync(Guid managerUserId, Guid projectId, int revision, IReadOnlyList<MilestoneInput> inputs, string? reason, CancellationToken cancellationToken)
        => MutateProjectAsync(projectId, revision, async (project, now, token) =>
        {
            if (project.Status is InstallationStatuses.PendingCustomerAcceptance or InstallationStatuses.Completed)
                return Fail(InstallationErrorCodes.MilestonesLocked, "Milestones cannot change while waiting for or after acceptance.");
            var started = project.Status is InstallationStatuses.InProgress or InstallationStatuses.ReworkRequired;
            if (started && string.IsNullOrWhiteSpace(reason))
                return Fail(InstallationErrorCodes.MilestonesLocked, "The project has started: a milestone revision needs a reason.");
            if (inputs.Count is < 1 or > 20 || inputs.Sum(i => i.WeightPercent) != 100 || inputs.Any(i => i.WeightPercent is < 0 or > 100 || string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 100))
                return Fail(InstallationErrorCodes.InvalidMilestones, "Provide 1–20 milestones with names, weights 0–100 that total exactly 100.");

            var existing = await unitOfWork.GetMilestonesAsync(projectId, true, token);
            var keptIds = inputs.Where(i => i.MilestoneId is not null).Select(i => i.MilestoneId!.Value).ToHashSet();
            if (keptIds.Any(id => existing.All(m => m.Id != id))) return Fail(InstallationErrorCodes.InvalidMilestones, "Unknown milestone id.");
            if (existing.Any(m => m.IsMandatory && !keptIds.Contains(m.Id))) return Fail(InstallationErrorCodes.InvalidMilestones, "Mandatory safety milestones cannot be removed.");
            if (started && existing.Any(m => m.Status != MilestoneStatuses.NotStarted && !keptIds.Contains(m.Id)))
                return Fail(InstallationErrorCodes.InvalidMilestones, "Started or completed milestones cannot be removed.");

            foreach (var removed in existing.Where(m => !keptIds.Contains(m.Id))) unitOfWork.Remove(removed);
            var order = 0;
            var custom = existing.Count(m => m.Code.StartsWith("CUSTOM_", StringComparison.Ordinal));
            foreach (var input in inputs)
            {
                order++;
                var milestone = input.MilestoneId is { } id ? existing.Single(m => m.Id == id) : null;
                if (milestone is null)
                {
                    unitOfWork.Add(new InstallationMilestone
                    {
                        Id = Guid.NewGuid(), ProjectId = projectId, SortOrder = 1000 + order, Code = $"CUSTOM_{++custom}", Name = input.Name.Trim(),
                        Description = Trim(input.Description, 1000), WeightPercent = input.WeightPercent, IsMandatory = false, Status = MilestoneStatuses.NotStarted,
                    });
                    continue;
                }
                if (milestone.Status == MilestoneStatuses.Completed && (milestone.WeightPercent != input.WeightPercent || milestone.Name != input.Name.Trim()))
                    return Fail(InstallationErrorCodes.InvalidMilestones, "Completed milestones cannot be renamed or re-weighted.");
                milestone.SortOrder = 1000 + order;
                milestone.Name = input.Name.Trim();
                milestone.Description = Trim(input.Description, 1000);
                milestone.WeightPercent = input.WeightPercent;
                milestone.Revision++;
            }
            await unitOfWork.SaveChangesAsync(token);
            // Normalise sort order (two passes keep the unique (project, sort_order) index satisfied).
            foreach (var m in (await unitOfWork.GetMilestonesAsync(projectId, true, token)).OrderBy(m => m.SortOrder).Select((m, i) => (m, i)))
                m.m.SortOrder = m.i + 1;
            Event(projectId, started ? "MILESTONES_REVISED" : "MILESTONES_EDITED", managerUserId, Trim(reason, 500), now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    public async Task<OperationResult<InstallationProjectView>> UpdateChecklistItemAsync(Guid managerUserId, Guid projectId, Guid itemId, int revision, ChecklistUpdate update, CancellationToken cancellationToken)
    {
        var status = update.Status?.Trim().ToUpperInvariant();
        if (status is not (ChecklistStatuses.Pending or ChecklistStatuses.Satisfied or ChecklistStatuses.NotApplicable))
            return OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed, "Status must be PENDING, SATISFIED or NOT_APPLICABLE.");
        if (status == ChecklistStatuses.Satisfied)
        {
            if (update.EvidenceFileId is not { } fileId)
                return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.EvidenceRequired, "A verified document is required to mark an item satisfied.");
            var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.ProjectDocument, null, cancellationToken);
            if (!verified.Succeeded) return verified.As<InstallationProjectView>();
        }
        return await MutateProjectAsync(projectId, revision, async (project, now, token) =>
        {
            if (project.Status == InstallationStatuses.Completed) return Fail(InstallationErrorCodes.InvalidState, "Completed projects are final.");
            var item = (await unitOfWork.GetChecklistAsync(projectId, true, token)).FirstOrDefault(i => i.Id == itemId);
            if (item is null) return Fail(InstallationErrorCodes.ProjectNotFound, "Checklist item was not found.");
            item.IsRequired = update.IsRequired;
            item.Status = status!;
            item.EvidenceFileId = status == ChecklistStatuses.Satisfied ? update.EvidenceFileId : null;
            item.Note = Trim(update.Note, 1000);
            item.VerifiedByUserId = status == ChecklistStatuses.Pending ? null : managerUserId;
            item.VerifiedAt = status == ChecklistStatuses.Pending ? null : now;
            Event(projectId, "CHECKLIST_UPDATED", managerUserId, $"{item.Code}: required={item.IsRequired}, {item.Status}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== Technician (team / leader)

    public async Task<IReadOnlyList<InstallationProjectView>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
    {
        var list = new List<InstallationProjectView>();
        foreach (var p in await unitOfWork.ListForTechnicianAsync(technicianUserId, cancellationToken)) list.Add(await ViewAsync(p.Id, staff: false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<InstallationProjectView>> GetForTechnicianAsync(Guid technicianUserId, Guid projectId, CancellationToken cancellationToken)
        => (await unitOfWork.GetTeamAsync(projectId, true, false, cancellationToken)).Any(m => m.TechnicianUserId == technicianUserId)
            ? OperationResult<InstallationProjectView>.Ok(await ViewAsync(projectId, staff: false, cancellationToken))
            : NotFound();

    /// <summary>SCHEDULED → IN_PROGRESS (leader). Required checklist items must be satisfied or explicitly not applicable.</summary>
    public Task<OperationResult<InstallationProjectView>> StartProjectAsync(Guid leaderUserId, Guid projectId, CancellationToken cancellationToken)
        => LeaderActionAsync(leaderUserId, projectId, async (project, now, token) =>
        {
            if (project.Status == InstallationStatuses.InProgress) return OperationResult<bool>.Ok(true);
            if (project.Status != InstallationStatuses.Scheduled) return Fail(InstallationErrorCodes.InvalidState, "Only scheduled projects can start.");
            var blocking = (await unitOfWork.GetChecklistAsync(projectId, false, token)).Where(i => i.IsRequired && i.Status == ChecklistStatuses.Pending).Select(i => i.Code).ToList();
            if (blocking.Count > 0) return OperationResult<bool>.Fail(InstallationErrorCodes.ChecklistIncomplete, "Required checklist items are not satisfied.", new { blocking });
            project.Status = InstallationStatuses.InProgress;
            project.StartedAt = now;
            Event(projectId, "PROJECT_STARTED", leaderUserId, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    public Task<OperationResult<InstallationProjectView>> StartMilestoneAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => MilestoneActionAsync(leaderUserId, projectId, milestoneId, null, (milestone, now, _) =>
        {
            if (milestone.Status == MilestoneStatuses.InProgress) return Task.FromResult(OperationResult<bool>.Ok(true));
            if (milestone.Status != MilestoneStatuses.NotStarted) return Task.FromResult(Fail(InstallationErrorCodes.InvalidState, "The milestone is already completed."));
            milestone.Status = MilestoneStatuses.InProgress;
            milestone.StartedAt = now;
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, "MILESTONE_STARTED", cancellationToken);

    public Task<OperationResult<InstallationProjectView>> UpdateProgressAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, int revision, int percent, string? notes, CancellationToken cancellationToken)
    {
        if (percent is < 0 or > 99)
            return Task.FromResult(OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed, "Progress must be 0–99 (100 is set by completing the milestone)."));
        return MilestoneActionAsync(leaderUserId, projectId, milestoneId, revision, (milestone, _, _) =>
        {
            if (milestone.Status != MilestoneStatuses.InProgress) return Task.FromResult(Fail(InstallationErrorCodes.InvalidState, "Start the milestone first."));
            milestone.ProgressPercent = percent;
            milestone.Notes = Trim(notes, 2000);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, "MILESTONE_PROGRESS", cancellationToken);
    }

    public async Task<OperationResult<InstallationProjectView>> AddEvidenceAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, Guid fileId, string? caption, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.InstallationEvidence, leaderUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<InstallationProjectView>();
        return await MilestoneActionAsync(leaderUserId, projectId, milestoneId, null, async (milestone, now, token) =>
        {
            if (milestone.Status != MilestoneStatuses.InProgress) return Fail(InstallationErrorCodes.InvalidState, "Evidence is added to a milestone in progress.");
            if (await unitOfWork.IsEvidenceFileUsedAsync(fileId, token)) return Fail(FileErrorCodes.AlreadyLinked, "This photo is already used as evidence.");
            unitOfWork.Add(new MilestoneEvidence
            {
                Id = Guid.NewGuid(), MilestoneId = milestone.Id, ProjectId = projectId, FileId = fileId, ReworkRound = milestone.ReworkRound,
                Caption = Trim(caption, 300), AddedByUserId = leaderUserId, AddedAt = now,
            });
            return OperationResult<bool>.Ok(true);
        }, "EVIDENCE_ADDED", cancellationToken);
    }

    /// <summary>Requires at least one verified photo added in the current round. Repeating it is a no-op.</summary>
    public Task<OperationResult<InstallationProjectView>> CompleteMilestoneAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => MilestoneActionAsync(leaderUserId, projectId, milestoneId, null, async (milestone, now, token) =>
        {
            if (milestone.Status == MilestoneStatuses.Completed) return OperationResult<bool>.Fail("NOOP", "already completed");
            if (milestone.Status != MilestoneStatuses.InProgress) return Fail(InstallationErrorCodes.InvalidState, "Start the milestone first.");
            if (!(await unitOfWork.GetEvidenceAsync(projectId, token)).Any(e => e.MilestoneId == milestone.Id && e.ReworkRound == milestone.ReworkRound))
                return Fail(InstallationErrorCodes.EvidenceRequired, "At least one verified photo is required to complete the milestone.");
            milestone.Status = MilestoneStatuses.Completed;
            milestone.ProgressPercent = 100;
            milestone.CompletedAt = now;
            milestone.CompletedByUserId = leaderUserId;
            return OperationResult<bool>.Ok(true);
        }, "MILESTONE_COMPLETED", cancellationToken);

    /// <summary>During rework: reopens a completed milestone; completing it again needs new evidence (old evidence stays in history).</summary>
    public Task<OperationResult<InstallationProjectView>> ReopenMilestoneAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => MilestoneActionAsync(leaderUserId, projectId, milestoneId, null, (milestone, now, _) =>
        {
            if (milestone.Status != MilestoneStatuses.Completed) return Task.FromResult(Fail(InstallationErrorCodes.InvalidState, "Only completed milestones are reopened."));
            milestone.Status = MilestoneStatuses.InProgress;
            milestone.ReworkRound++;
            milestone.ProgressPercent = 0;
            milestone.CompletedAt = null;
            milestone.CompletedByUserId = null;
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, "MILESTONE_REOPENED", cancellationToken, requireRework: true);

    public async Task<OperationResult<InstallationProjectView>> RegisterAssetsAsync(Guid leaderUserId, Guid projectId, IReadOnlyList<AssetInput> assets, CancellationToken cancellationToken)
    {
        if (assets.Count is < 1 or > 500)
            return OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed, "Register between 1 and 500 assets at a time.");
        foreach (var a in assets)
        {
            if (!AssetTypes.All.Contains(a.AssetType?.Trim().ToUpperInvariant() ?? "") || string.IsNullOrWhiteSpace(a.Manufacturer) || string.IsNullOrWhiteSpace(a.Model)
                || string.IsNullOrWhiteSpace(a.SerialNumber) || a.SerialNumber.Length > 100 || a.WarrantyMonths is < 0 or > 600)
                return OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed,
                    $"Each asset needs a type ({string.Join(", ", AssetTypes.All)}), manufacturer, model, serial number (≤100) and a warranty of 0–600 months.");
        }
        var keys = assets.Select(a => (a.Manufacturer.Trim().ToUpperInvariant(), a.SerialNumber.Trim().ToUpperInvariant())).ToList();
        if (keys.Distinct().Count() != keys.Count)
            return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.DuplicateSerial, "The same serial number appears twice in the request.");

        return await LeaderActionAsync(leaderUserId, projectId, async (project, now, token) =>
        {
            if (project.Status is not (InstallationStatuses.InProgress or InstallationStatuses.ReworkRequired))
                return Fail(InstallationErrorCodes.InvalidState, "Assets are registered while the installation is in progress.");
            foreach (var (manufacturer, serial) in keys)
                if (await unitOfWork.SerialExistsAsync(manufacturer, serial, token))
                    return OperationResult<bool>.Fail(InstallationErrorCodes.DuplicateSerial, $"Serial {serial} from {manufacturer} is already registered.");
            foreach (var a in assets)
                unitOfWork.Add(new InstalledAsset
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, AssetType = a.AssetType.Trim().ToUpperInvariant(), ProductId = a.ProductId,
                    Manufacturer = a.Manufacturer.Trim().ToUpperInvariant(), Model = a.Model.Trim(), SerialNumber = a.SerialNumber.Trim().ToUpperInvariant(),
                    PositionCode = Trim(a.PositionCode, 50), WarrantyMonths = a.WarrantyMonths, RegisteredByUserId = leaderUserId, RegisteredAt = now,
                });
            Event(projectId, "ASSETS_REGISTERED", leaderUserId, $"{assets.Count} assets", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>IN_PROGRESS/REWORK_REQUIRED → PENDING_CUSTOMER_ACCEPTANCE: mandatory milestones done, nothing half-done, assets registered.</summary>
    public async Task<OperationResult<InstallationProjectView>> RequestAcceptanceAsync(Guid leaderUserId, Guid projectId, string completionReport, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(completionReport) || completionReport.Length > 8000)
            return OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed, "A completion report (≤ 8000 characters) is required.");
        return await LeaderActionAsync(leaderUserId, projectId, async (project, now, token) =>
        {
            if (project.Status is not (InstallationStatuses.InProgress or InstallationStatuses.ReworkRequired))
                return Fail(InstallationErrorCodes.InvalidState, "Acceptance is requested for a project in progress or in rework.");
            var milestones = await unitOfWork.GetMilestonesAsync(projectId, false, token);
            var missing = milestones.Where(m => (m.IsMandatory && m.Status != MilestoneStatuses.Completed) || m.Status == MilestoneStatuses.InProgress).Select(m => m.Code).ToList();
            if (missing.Count > 0) return OperationResult<bool>.Fail(InstallationErrorCodes.MilestonesIncomplete, "Mandatory milestones must be completed and none left in progress.", new { missing });
            var assets = await unitOfWork.GetAssetsAsync(projectId, token);
            if (options.RequirePanelAndInverterAssets && (!assets.Any(a => a.AssetType == "PANEL") || !assets.Any(a => a.AssetType == "INVERTER")))
                return Fail(InstallationErrorCodes.AssetsRequired, "Register the installed panels and inverter before requesting acceptance.");
            if ((await unitOfWork.GetAcceptancesAsync(projectId, false, token)).Any(a => a.Decision == AcceptanceDecisions.Pending))
                return Fail(InstallationErrorCodes.InvalidState, "An acceptance request is already pending.");

            project.AcceptanceRound++;
            unitOfWork.Add(new InstallationAcceptance
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Round = project.AcceptanceRound, RequestedByUserId = leaderUserId, RequestedAt = now,
                CompletionReport = completionReport.Trim(), Decision = AcceptanceDecisions.Pending,
            });
            project.Status = InstallationStatuses.PendingCustomerAcceptance;
            project.AcceptanceRequestedAt = now;
            Event(projectId, "ACCEPTANCE_REQUESTED", leaderUserId, $"round {project.AcceptanceRound}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ================================================================== Customer

    public async Task<IReadOnlyList<InstallationProjectView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<InstallationProjectView>();
        foreach (var p in await unitOfWork.ListForCustomerAsync(customerUserId, cancellationToken)) list.Add(await ViewAsync(p.Id, staff: false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<InstallationProjectView>> GetForCustomerAsync(Guid customerUserId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        return project is null || project.CustomerUserId != customerUserId ? NotFound() : OperationResult<InstallationProjectView>.Ok(await ViewAsync(projectId, false, cancellationToken));
    }

    /// <summary>PENDING_CUSTOMER_ACCEPTANCE → COMPLETED (handover recorded) or → REWORK_REQUIRED (reason required). Decisions are immutable.</summary>
    public async Task<OperationResult<InstallationProjectView>> DecideAcceptanceAsync(Guid customerUserId, Guid projectId, bool accept, string? reason, CancellationToken cancellationToken)
    {
        if (!accept && string.IsNullOrWhiteSpace(reason))
            return OperationResult<InstallationProjectView>.Fail(CommonErrorCodes.ValidationFailed, "Describe what needs to be fixed.");
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || project.CustomerUserId != customerUserId) return NotFound();

        return await MutateProjectAsync(projectId, null, async (p, now, token) =>
        {
            if (p.Status != InstallationStatuses.PendingCustomerAcceptance) return Fail(InstallationErrorCodes.InvalidState, "There is no installation waiting for your acceptance.");
            var pending = (await unitOfWork.GetAcceptancesAsync(projectId, true, token)).SingleOrDefault(a => a.Decision == AcceptanceDecisions.Pending);
            if (pending is null) return Fail(InstallationErrorCodes.InvalidState, "There is no pending acceptance request.");
            pending.DecidedByUserId = customerUserId;
            pending.DecidedAt = now;
            pending.Reason = Trim(reason, 2000);
            if (accept)
            {
                pending.Decision = AcceptanceDecisions.Accepted;
                p.Status = InstallationStatuses.Completed;
                p.CompletedAt = now;
                p.HandoverAt = now;
                // Free any future team bookings that are no longer needed.
                foreach (var a in (await unitOfWork.GetAssignmentsAsync(projectId, true, true, token)).Where(a => a.StartsAt > now))
                {
                    a.Status = "RELEASED";
                    a.ReleasedAt = now;
                    await scheduling.RequestRecalculationAsync(a.TechnicianUserId, calendar.LocalDateOf(a.StartsAt), "INSTALLATION_SCHEDULE_CHANGED", now, token);
                }
                Event(projectId, "CUSTOMER_ACCEPTED", customerUserId, $"round {pending.Round}", now);
            }
            else
            {
                pending.Decision = AcceptanceDecisions.ReworkRequested;
                p.Status = InstallationStatuses.ReworkRequired;
                Event(projectId, "REWORK_REQUESTED", customerUserId, Trim(reason, 500), now);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    public async Task<OperationResult<HandoverView>> GetHandoverAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || (!isStaff && project.CustomerUserId != userId))
            return OperationResult<HandoverView>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
        if (project.Status != InstallationStatuses.Completed || project.HandoverAt is not { } handover)
            return OperationResult<HandoverView>.Fail(InstallationErrorCodes.InvalidState, "The installation has not been handed over yet.");
        var start = calendar.LocalDateOf(handover);
        var assets = (await unitOfWork.GetAssetsAsync(projectId, cancellationToken))
            .Select(a => new HandoverAssetView(a.AssetType, a.Manufacturer, a.Model, a.SerialNumber, a.PositionCode, a.WarrantyMonths,
                a.WarrantyMonths is { } months ? start.AddMonths(months) : null)).ToList();
        return OperationResult<HandoverView>.Ok(new HandoverView(projectId, project.ContractId, handover, assets));
    }

    // ================================================================== internals

    private async Task<OperationResult<(InstallationProject Project, GeoPoint Site, IReadOnlyList<PlannedShift> Shifts, IReadOnlySet<Guid> OwnAssignments)>> PlanningContextAsync(
        Guid projectId, IReadOnlyList<Guid> members, Guid leaderId, IReadOnlyList<ShiftInput> shifts, CancellationToken cancellationToken)
    {
        static OperationResult<(InstallationProject, GeoPoint, IReadOnlyList<PlannedShift>, IReadOnlySet<Guid>)> Error(string code, string message, object? details = null)
            => OperationResult<(InstallationProject, GeoPoint, IReadOnlyList<PlannedShift>, IReadOnlySet<Guid>)>.Fail(code, message, details);

        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null) return Error(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
        if (project.Status is InstallationStatuses.PendingCustomerAcceptance or InstallationStatuses.Completed)
            return Error(InstallationErrorCodes.InvalidState, "The schedule can no longer be changed.");
        var team = members.Distinct().ToList();
        if (team.Count < 1 || team.Count > options.MaxTeamSize || !team.Contains(leaderId))
            return Error(InstallationErrorCodes.InvalidTeam, $"Choose 1–{options.MaxTeamSize} technicians and exactly one leader among them.");
        foreach (var technicianId in team)
        {
            var technician = await scheduling.GetTechnicianAsync(technicianId, cancellationToken);
            if (technician is null || !technician.HasTechnicianRole || !technician.IsActiveUser || technician.Profile is not { IsSchedulingActive: true })
                return Error(InstallationErrorCodes.InvalidTeam, "Every team member must be an active technician with scheduling enabled.", new { technicianUserId = technicianId });
        }

        var now = time.GetUtcNow();
        var today = calendar.LocalDateOf(now);
        if (shifts.Count < 1 || shifts.Count > options.MaxShifts) return Error(InstallationErrorCodes.InvalidShifts, $"Provide 1–{options.MaxShifts} shifts.");
        var planned = planner.ToPlannedShifts(shifts);
        if (planned.Any(s => s.LocalEnd <= s.LocalStart)) return Error(InstallationErrorCodes.InvalidShifts, "Every shift must end after it starts.");
        if (planned.Any(s => s.StartsAtUtc <= now || s.Date > today.AddDays(options.MaxPlanningHorizonDays)))
            return Error(InstallationErrorCodes.InvalidShifts, $"Shifts must be in the future and within {options.MaxPlanningHorizonDays} days.");
        foreach (var day in planned.GroupBy(s => s.Date))
        {
            var ordered = day.OrderBy(s => s.StartsAtUtc).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].StartsAtUtc < ordered[i - 1].EndsAtUtc) return Error(InstallationErrorCodes.InvalidShifts, $"Shifts on {day.Key:yyyy-MM-dd} overlap.");
        }

        var (lat, lon) = await unitOfWork.GetSiteLocationAsync(project.PropertySiteId, cancellationToken);
        if (GeoPoint.From(lat, lon) is not { } site) return Error(InstallationErrorCodes.InvalidState, "The installation site has no valid GPS coordinates.");
        var own = (await unitOfWork.GetAssignmentsAsync(projectId, true, false, cancellationToken)).Where(a => a.StartsAt > now).Select(a => a.Id).ToHashSet();
        return OperationResult<(InstallationProject, GeoPoint, IReadOnlyList<PlannedShift>, IReadOnlySet<Guid>)>.Ok((project, site, planned, own));
    }

    private async Task ReplaceTeamAsync(Guid projectId, IReadOnlyList<Guid> team, Guid leaderId, Guid managerUserId, DateTimeOffset now, CancellationToken token)
    {
        var current = await unitOfWork.GetTeamAsync(projectId, true, true, token);
        foreach (var member in current)
        {
            member.IsLeader = false;
            if (!team.Contains(member.TechnicianUserId)) member.RemovedAt = now;
        }
        await unitOfWork.SaveChangesAsync(token);
        foreach (var technicianId in team)
        {
            var existing = current.FirstOrDefault(m => m.TechnicianUserId == technicianId);
            if (existing is not null) existing.IsLeader = technicianId == leaderId;
            else unitOfWork.Add(new InstallationTeamMember
            {
                Id = Guid.NewGuid(), ProjectId = projectId, TechnicianUserId = technicianId, IsLeader = technicianId == leaderId,
                AssignedByUserId = managerUserId, AssignedAt = now,
            });
        }
    }

    private async Task<OperationResult<InstallationProjectView>> LeaderActionAsync(Guid leaderUserId, Guid projectId,
        Func<InstallationProject, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        var team = await unitOfWork.GetTeamAsync(projectId, true, false, cancellationToken);
        if (team.All(m => m.TechnicianUserId != leaderUserId)) return NotFound();
        if (!team.Any(m => m.TechnicianUserId == leaderUserId && m.IsLeader))
            return OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.NotLeader, "Only the team leader can do this.");
        return await MutateProjectAsync(projectId, null, async (project, now, token) =>
        {
            // Re-check leadership inside the transaction (the manager may have changed it).
            if (!(await unitOfWork.GetTeamAsync(projectId, true, false, token)).Any(m => m.TechnicianUserId == leaderUserId && m.IsLeader))
                return Fail(InstallationErrorCodes.NotLeader, "Only the team leader can do this.");
            return await change(project, now, token);
        }, cancellationToken);
    }

    private Task<OperationResult<InstallationProjectView>> MilestoneActionAsync(Guid leaderUserId, Guid projectId, Guid milestoneId, int? revision,
        Func<InstallationMilestone, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, string eventType, CancellationToken cancellationToken, bool requireRework = false)
        => LeaderActionAsync(leaderUserId, projectId, async (project, now, token) =>
        {
            if (requireRework ? project.Status != InstallationStatuses.ReworkRequired : project.Status is not (InstallationStatuses.InProgress or InstallationStatuses.ReworkRequired))
                return Fail(InstallationErrorCodes.InvalidState, requireRework ? "Milestones are reopened only during rework." : "Start the project first.");
            var milestone = (await unitOfWork.GetMilestonesAsync(projectId, true, token)).FirstOrDefault(m => m.Id == milestoneId);
            if (milestone is null) return Fail(InstallationErrorCodes.MilestoneNotFound, "Milestone was not found.");
            if (revision is { } expected && milestone.Revision != expected)
                return OperationResult<bool>.Fail(InstallationErrorCodes.StaleRevision, "The milestone changed meanwhile. Reload and try again.", new { currentRevision = milestone.Revision });
            var result = await change(milestone, now, token);
            if (!result.Succeeded) return result;
            milestone.Revision++;
            Event(projectId, eventType, leaderUserId, milestone.Code, now);
            return result;
        }, cancellationToken);

    /// <summary>Loads the project tracked, applies the change, bumps the revision and commits ("NOOP" results return the current view unchanged).</summary>
    private async Task<OperationResult<InstallationProjectView>> MutateProjectAsync(Guid projectId, int? revision,
        Func<InstallationProject, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var project = await unitOfWork.FindProjectAsync(projectId, true, token);
                if (project is null) return (Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found."), false);
                if (revision is { } expected && project.Revision != expected)
                    return (OperationResult<bool>.Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.", new { currentRevision = project.Revision }), false);
                var now = time.GetUtcNow();
                var result = await change(project, now, token);
                if (!result.Succeeded) return (result, false);
                project.UpdatedAt = now;
                project.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            if (!outcome.Succeeded && outcome.ErrorCode != "NOOP") return outcome.As<InstallationProjectView>();
        }
        catch (PersistenceConflictException ex)
        {
            return ex.Kind == PersistenceConflictException.UniqueViolation && ex.Constraint?.Contains("serial", StringComparison.OrdinalIgnoreCase) == true
                ? OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.DuplicateSerial, "A serial number is already registered.")
                : OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.StaleRevision, "The project changed meanwhile. Reload and try again.");
        }
        return OperationResult<InstallationProjectView>.Ok(await ViewAsync(projectId, staff: true, cancellationToken));
    }

    private async Task<InstallationProjectView> ViewAsync(Guid projectId, bool staff, CancellationToken cancellationToken)
    {
        var p = (await unitOfWork.FindProjectAsync(projectId, false, cancellationToken))!;
        var team = await unitOfWork.GetTeamAsync(projectId, true, false, cancellationToken);
        var names = await unitOfWork.GetUserNamesAsync(team.Select(m => m.TechnicianUserId).ToList(), cancellationToken);
        var milestones = await unitOfWork.GetMilestonesAsync(projectId, false, cancellationToken);
        var evidence = await unitOfWork.GetEvidenceAsync(projectId, cancellationToken);
        var progress = milestones.Sum(m => m.WeightPercent * (m.Status == MilestoneStatuses.Completed ? 100 : m.ProgressPercent)) / 100;
        return new InstallationProjectView(p.Id, p.ContractId, p.SurveyRequestId, p.CustomerUserId, p.Status, progress, p.PlannedStartDate, p.PlannedEndDate,
            p.StartedAt, p.AcceptanceRequestedAt, p.CompletedAt, p.HandoverAt, p.Revision,
            team.Select(m => new TeamMemberView(m.TechnicianUserId, names.GetValueOrDefault(m.TechnicianUserId, ""), m.IsLeader, m.AssignedAt)).ToList(),
            (await unitOfWork.GetShiftsAsync(projectId, false, cancellationToken)).Where(s => s.Status == "ACTIVE").OrderBy(s => s.StartsAt)
                .Select(s => new ShiftView(s.Id, s.LocalDate, s.LocalStart.ToString("HH:mm"), s.LocalEnd.ToString("HH:mm"),
                    TimeZoneInfo.ConvertTime(s.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(s.EndsAt, calendar.TimeZone), s.Status)).ToList(),
            milestones.OrderBy(m => m.SortOrder).Select(m => new MilestoneView(m.Id, m.SortOrder, m.Code, m.Name, m.Description, m.WeightPercent, m.IsMandatory,
                m.Status, m.Status == MilestoneStatuses.Completed ? 100 : m.ProgressPercent, m.Notes, m.ReworkRound, m.StartedAt, m.CompletedAt, m.Revision,
                evidence.Where(e => e.MilestoneId == m.Id).OrderBy(e => e.AddedAt).Select(e => new EvidenceView(e.Id, e.FileId, e.ReworkRound, e.Caption, e.AddedAt)).ToList())).ToList(),
            (await unitOfWork.GetAcceptancesAsync(projectId, false, cancellationToken)).OrderBy(a => a.Round)
                .Select(a => new AcceptanceView(a.Round, a.RequestedAt, a.CompletionReport, a.Decision, a.DecidedAt, a.Reason)).ToList(),
            (await unitOfWork.GetAssetsAsync(projectId, cancellationToken))
                .Select(a => new AssetView(a.Id, a.AssetType, a.ProductId, a.Manufacturer, a.Model, a.SerialNumber, a.PositionCode, a.WarrantyMonths)).ToList(),
            staff ? (await unitOfWork.GetChecklistAsync(projectId, false, cancellationToken))
                .Select(i => new ChecklistItemView(i.Id, i.Code, i.Title, i.IsRequired, i.Status, i.EvidenceFileId, i.Note, i.VerifiedAt)).ToList() : null,
            staff ? (await unitOfWork.GetEventsAsync(projectId, cancellationToken)).Select(e => new InstallationEventView(e.EventType, e.ActorUserId, e.Detail, e.CreatedAt)).ToList() : null);
    }

    private SchedulePreviewView Preview(SchedulePlan plan) => new(plan.IsFeasible, plan.Conflicts,
        plan.Placements.Select(p => new PlacementView(p.TechnicianUserId, p.Shift.Date, p.Shift.LocalStart.ToString("HH:mm"), p.Shift.LocalEnd.ToString("HH:mm"),
            p.Plan.Inbound.OriginKind, p.Inbound.Duration is { } d ? (int)Math.Ceiling(d.TotalSeconds) : null,
            p.Inbound.DistanceMeters is { } m ? Math.Round((decimal)m, 1) : null,
            p.EarliestArrivalUtc is { } at ? TimeZoneInfo.ConvertTime(at, calendar.TimeZone) : null)).ToList());

    private void Event(Guid projectId, string type, Guid actor, string? detail, DateTimeOffset now)
        => unitOfWork.Add(new InstallationEvent { Id = Guid.NewGuid(), ProjectId = projectId, EventType = type, ActorUserId = actor, Detail = Trim(detail, 500), CreatedAt = now });

    private static OperationResult<bool> Fail(string code, string message) => OperationResult<bool>.Fail(code, message);
    private static OperationResult<InstallationProjectView> NotFound() => OperationResult<InstallationProjectView>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
