using System.Text.Json;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.Installations;

/// <summary>The customer's decision on a reported completion (2026-11-19).</summary>
public static class CustomerAcceptanceDecisions
{
    public const string Accept = "ACCEPT";
    /// <summary>Not accepted: the work must be redone (reason required, evidence optional).</summary>
    public const string RequestRework = "REQUEST_REWORK";
    /// <summary>Same effect as <see cref="RequestRework"/> (the FE may label it "reject").</summary>
    public const string Reject = "REJECT";
    public static readonly IReadOnlyList<string> All = [Accept, RequestRework, Reject];
}

/// <param name="Decision">ACCEPT, REQUEST_REWORK or REJECT.</param>
/// <param name="Reason">Required for REQUEST_REWORK / REJECT (≤ 2000), optional note for ACCEPT.</param>
/// <param name="EvidenceFileIds">VERIFIED ACCEPTANCE_EVIDENCE files the customer uploaded (≤ 10), for a rework request.</param>
/// <param name="ExpectedRevision">The project revision the customer saw (stale → 409).</param>
public sealed record CustomerAcceptanceInput(string? Decision, string? Reason, IReadOnlyList<Guid>? EvidenceFileIds, int? ExpectedRevision);

public sealed partial class InstallationHandlers
{
    private static readonly JsonSerializerOptions AcceptanceJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Customer acceptance (restored 2026-11-19; replaces the Manager's internal acceptance). Only the project's customer decides, on the pending round
    /// (each round keeps its own report, decision, decider, time, reason and evidence):
    /// <list type="bullet">
    /// <item>ACCEPT → COMPLETED (handover, assignment closed). Same guards as before: every required prerequisite SATISFIED / NOT_APPLICABLE and any
    /// equipment difference approved. The acceptance time is the official one; for a 30/70 contract it opens the balance (due ≤ 72 h later) in the same
    /// transaction.</item>
    /// <item>REQUEST_REWORK / REJECT → REWORK_REQUIRED with the reason (and evidence); the technician reports a new round.</item>
    /// </list>
    /// The technician never accepts for the customer; a Manager only returns a report to rework.
    /// </summary>
    public async Task<OperationResult<InstallationCustomerView>> DecideAcceptanceAsync(Guid customerUserId, Guid projectId, CustomerAcceptanceInput input,
        CancellationToken cancellationToken)
    {
        var decision = input.Decision?.Trim().ToUpperInvariant();
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        var evidence = input.EvidenceFileIds?.Distinct().ToList() ?? [];
        var errors = new Dictionary<string, string[]>();
        if (decision is null || !CustomerAcceptanceDecisions.All.Contains(decision)) errors["decision"] = [$"One of {string.Join(", ", CustomerAcceptanceDecisions.All)}."];
        if (decision is not CustomerAcceptanceDecisions.Accept && decision is not null && reason is null) errors["reason"] = ["Tell the team what must be fixed."];
        if (reason is { Length: > 2000 }) errors["reason"] = ["At most 2000 characters."];
        if (evidence.Count > 10) errors["evidenceFileIds"] = ["At most 10 files."];
        if (decision == CustomerAcceptanceDecisions.Accept && evidence.Count > 0) errors["evidenceFileIds"] = ["Evidence is attached to a rework request only."];
        if (errors.Count > 0) return OperationResult<InstallationCustomerView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || project.CustomerUserId != customerUserId) return NotFound<InstallationCustomerView>();
        if (evidence.Count > 0)
        {
            var verified = await files.RequireVerifiedAsync(evidence, FilePurposes.AcceptanceEvidence, customerUserId, cancellationToken);
            if (!verified.Succeeded) return verified.As<InstallationCustomerView>();
        }

        var result = await MutateAsync(projectId, input.ExpectedRevision, null, async (p, now, token) =>
        {
            if (p.CustomerUserId != customerUserId) return Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
            if (p.Status != InstallationStatuses.PendingCustomerAcceptance)
                return Fail(InstallationErrorCodes.InvalidState, $"Only an installation whose completion was reported can be accepted or sent to rework; this one is {p.Status}.");
            var pending = (await unitOfWork.GetAcceptancesAsync(projectId, true, token)).SingleOrDefault(a => a.Decision == AcceptanceDecisions.Pending);
            if (pending is null) return Fail(InstallationErrorCodes.InvalidState, "There is no pending completion report.");
            pending.DecidedByUserId = customerUserId;
            pending.DecidedByRole = AcceptanceDeciders.Customer;
            pending.DecidedAt = now;
            pending.Reason = reason;
            var from = p.Status;
            if (decision == CustomerAcceptanceDecisions.Accept)
            {
                var open = (await unitOfWork.GetChecklistAsync(projectId, false, token))
                    .Where(i => i.IsRequired && i.Status is not (ChecklistStatuses.Satisfied or ChecklistStatuses.NotApplicable)).Select(i => i.Code).ToList();
                if (open.Count > 0)
                    return OperationResult<bool>.Fail(InstallationErrorCodes.ChecklistIncomplete,
                        "SmartSolar must still complete required paperwork (grid / fire safety / structural) before the installation can be accepted.", new { missing = open });
                if (await EquipmentComparisonAsync(p, token) is { Status: EquipmentComparisonStatuses.ApprovalRequired } comparison)
                    return OperationResult<bool>.Fail(InstallationErrorCodes.SubstitutionApprovalRequired,
                        "The installed equipment differs from the contract and SmartSolar has not approved the difference yet.", new { differences = comparison.Differences });
                pending.Decision = AcceptanceDecisions.Accepted;
                p.Status = InstallationStatuses.Completed;
                p.CompletedAt = now;
                p.HandoverAt = now;
                if (await unitOfWork.GetCurrentAssignmentAsync(projectId, true, token) is { } current)
                {
                    current.Status = AssignmentStatuses.Completed;
                    current.ClosedAt = now;
                }
                Event(projectId, InstallationEventTypes.CustomerAccepted, customerUserId, from, p.Status,
                    Trim($"round {pending.Round}: accepted by the customer" + (reason is null ? "" : $"; {reason}"), 500), now);
                // 30/70 plan: the official acceptance time opens the balance (same transaction; refused → nothing is recorded).
                if (acceptanceHook is not null && await acceptanceHook.CustomerAcceptedAsync(p.ContractId, customerUserId, now, token) is { } refused)
                    return Fail(InstallationErrorCodes.InvalidState, refused);
                unitOfWork.AddNotification(p.CustomerUserId, CustomerNotificationTypes.InstallationCompleted,
                    "Bạn đã nghiệm thu công trình điện mặt trời. Hồ sơ bàn giao và bảo hành có trong ứng dụng.", now);
            }
            else
            {
                pending.Decision = AcceptanceDecisions.ReworkRequested;
                pending.EvidenceFileIds = evidence.Count == 0 ? null : JsonSerializer.Serialize(evidence, AcceptanceJson);
                p.Status = InstallationStatuses.ReworkRequired;
                Event(projectId, InstallationEventTypes.ReworkRequested, customerUserId, from, p.Status,
                    Trim($"round {pending.Round}: {reason}" + (evidence.Count == 0 ? "" : $"; evidence {string.Join(",", evidence)}"), 500), now);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return result.Succeeded
            ? OperationResult<InstallationCustomerView>.Ok(await CustomerViewAsync(projectId, cancellationToken))
            : result.As<InstallationCustomerView>();
    }
}
