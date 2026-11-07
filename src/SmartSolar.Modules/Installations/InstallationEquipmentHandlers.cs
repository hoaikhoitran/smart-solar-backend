using System.Security.Cryptography;
using System.Text;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Installations;

/// <summary>A contracted panel / inverter product whose installed count differs from the accepted quotation (installed 0 = not installed, contracted 0 = not contracted).</summary>
public sealed record EquipmentDifference(Guid ProductId, string ProductType, string? Name, decimal ContractedQuantity, int InstalledQuantity);

/// <summary>An approval step; <see cref="ActorUserId"/> is null in the customer's view (no staff identity).</summary>
public sealed record InstallationApprovalStep(Guid? ActorUserId, string? Reason, DateTimeOffset At);

/// <summary>
/// Installed panels and inverters against the contract's accepted quotation. <see cref="Fingerprint"/> names the exact contracted and installed
/// sets: an approval only counts for the same sets, so registering other equipment afterwards needs new approvals.
/// </summary>
public sealed record EquipmentComparisonView(string Status, IReadOnlyList<EquipmentDifference> Differences, string Fingerprint,
    InstallationApprovalStep? ManagerApproval, InstallationApprovalStep? CustomerApproval, IReadOnlyList<SubstitutionRecordView>? History = null);

/// <summary>A stored substitution decision (staff view): what was approved, by whom, and how it ended.</summary>
public sealed record SubstitutionRecordView(Guid SubstitutionId, string Status, string Fingerprint, Guid QuotationId, Guid ManagerUserId, string ManagerReason,
    DateTimeOffset ManagerApprovedAt, DateTimeOffset? CustomerApprovedAt, DateTimeOffset? CustomerDeclinedAt, string? CustomerDeclineReason,
    DateTimeOffset? SupersededAt, IReadOnlyList<EquipmentDifference> Items);

public static class EquipmentComparisonStatuses
{
    /// <summary>The installed panels and inverters are exactly the contracted products and quantities.</summary>
    public const string Matches = "MATCHES";
    /// <summary>The accepted quotation lists no catalog panel / inverter (legacy or custom lines): nothing to compare against.</summary>
    public const string NotComparable = "NOT_COMPARABLE";
    /// <summary>Differences without a Manager approval: completion cannot be reported.</summary>
    public const string ApprovalRequired = "SUBSTITUTION_APPROVAL_REQUIRED";
    /// <summary>The Manager approved the differences; the customer has not yet: no handover.</summary>
    public const string CustomerApprovalRequired = "CUSTOMER_APPROVAL_REQUIRED";
    /// <summary>Manager and customer approved exactly these differences.</summary>
    public const string Approved = "SUBSTITUTION_APPROVED";
    /// <summary>
    /// A project handed over before the equipment check existed, with differences nobody approved: shown as history, never as a pending
    /// requirement (its records are not rewritten).
    /// </summary>
    public const string NotCheckedAtHandover = "NOT_CHECKED_AT_HANDOVER";
}

public sealed partial class InstallationHandlers
{
    /// <summary>
    /// Manager approves the differences between installed and contracted equipment (substitution, other quantity) with a reason, while the
    /// work is in progress or in rework, or while a completion waits for the customer (a report made before this check existed). Any
    /// technically different product is a substitution: there is no approved equivalence rule, so the customer must approve it as well
    /// before handover.
    /// </summary>
    public async Task<OperationResult<InstallationStaffView>> ApproveSubstitutionAsync(Guid managerUserId, Guid projectId, int expectedRevision, string? reason,
        CancellationToken cancellationToken)
    {
        var why = Trim(reason, 400);
        if (why is null) return OperationResult<InstallationStaffView>.Fail(InstallationErrorCodes.ReasonRequired, "Explain why the installed equipment differs from the contract.");
        var result = await MutateAsync(projectId, expectedRevision, null, async (p, now, token) =>
        {
            if (!InstallationStatuses.IsStarted(p.Status) && p.Status != InstallationStatuses.PendingCustomerAcceptance)
                return Fail(InstallationErrorCodes.InvalidState, "Substitutions are approved before the handover, while work or the customer's acceptance is open.");
            var comparison = await EquipmentComparisonAsync(p, token);
            if (comparison.Status is EquipmentComparisonStatuses.Matches or EquipmentComparisonStatuses.NotComparable)
                return Fail(InstallationErrorCodes.NoSubstitution, "The installed equipment does not differ from the contract.");
            if (comparison.ManagerApproval is not null) return Fail(InstallationErrorCodes.InvalidState, "These differences are already approved.");
            // An approval of an earlier comparison no longer describes the installation: it ends (kept), then the new record is written.
            if (await unitOfWork.GetActiveSubstitutionAsync(projectId, true, token) is { } stale)
            {
                End(stale, EquipmentSubstitutionStatuses.Superseded, now);
                await unitOfWork.SaveChangesAsync(token);
            }
            var substitution = new EquipmentSubstitution
            {
                Id = Guid.NewGuid(), ProjectId = projectId, ContractId = p.ContractId, QuotationId = await unitOfWork.GetContractQuotationIdAsync(p.ContractId, token),
                Fingerprint = comparison.Fingerprint, Status = EquipmentSubstitutionStatuses.CustomerApprovalPending, ManagerUserId = managerUserId,
                ManagerReason = why, ManagerApprovedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            unitOfWork.Add(substitution);
            foreach (var d in comparison.Differences)
                unitOfWork.Add(new EquipmentSubstitutionItem
                {
                    Id = Guid.NewGuid(), SubstitutionId = substitution.Id, ProductId = d.ProductId == Guid.Empty ? null : d.ProductId, ProductType = d.ProductType,
                    Name = Trim(d.Name, 255), ContractedQuantity = d.ContractedQuantity, InstalledQuantity = d.InstalledQuantity,
                });
            Event(projectId, InstallationEventTypes.SubstitutionApproved, managerUserId, null, null, $"fp:{comparison.Fingerprint}; {why}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => StaffViewAsync(projectId, cancellationToken));
    }

    /// <summary>The customer agrees to the Manager-approved differences of the completion waiting for them (then accepts or asks for rework).</summary>
    public async Task<OperationResult<InstallationCustomerView>> CustomerApproveSubstitutionAsync(Guid customerUserId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null || project.CustomerUserId != customerUserId) return NotFound<InstallationCustomerView>();
        var result = await MutateAsync(projectId, null, null, async (p, now, token) =>
        {
            if (p.Status != InstallationStatuses.PendingCustomerAcceptance)
                return Fail(InstallationErrorCodes.InvalidState, "There is no installation waiting for your acceptance.");
            var comparison = await EquipmentComparisonAsync(p, token);
            if (comparison.Status == EquipmentComparisonStatuses.Approved) return OperationResult<bool>.Fail(Noop, "already approved");
            if (comparison.Status != EquipmentComparisonStatuses.CustomerApprovalRequired)
                return Fail(InstallationErrorCodes.NoSubstitution, "There is no approved substitution waiting for your agreement.");
            var active = (await unitOfWork.GetActiveSubstitutionAsync(projectId, true, token))!;
            active.Status = EquipmentSubstitutionStatuses.Approved;
            active.CustomerUserId = customerUserId;
            active.CustomerApprovedAt = now;
            active.UpdatedAt = now;
            active.Revision++;
            Event(projectId, InstallationEventTypes.SubstitutionCustomerApproved, customerUserId, null, null, $"fp:{comparison.Fingerprint}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        return await ThenAsync(result, () => CustomerViewAsync(projectId, cancellationToken));
    }

    /// <summary>
    /// PANEL / INVERTER assets registered by the installation itself (not by a later after-sales replacement, whatever their status now), per
    /// catalog product, against the accepted quotation's panels and inverters: the comparison of a handed-over project never changes afterwards.
    /// </summary>
    private async Task<EquipmentComparisonView> EquipmentComparisonAsync(InstallationProject p, CancellationToken cancellationToken)
    {
        var contracted = await unitOfWork.GetContractedEquipmentAsync(p.ContractId, cancellationToken);
        var installed = (await unitOfWork.GetAssetsAsync(p.Id, cancellationToken))
            .Where(a => a.ServiceRequestId is null && a.AssetType is AssetTypes.Panel or AssetTypes.Inverter)
            .GroupBy(a => (a.ProductId, a.AssetType)).ToList();
        var fingerprint = Fingerprint(contracted, installed.Select(g => ($"{g.Key.ProductId}", g.Key.AssetType, g.Count())));
        if (contracted.Count == 0) return new(EquipmentComparisonStatuses.NotComparable, [], fingerprint, null, null);

        var differences = new List<EquipmentDifference>();
        foreach (var c in contracted)
        {
            var count = installed.Where(g => g.Key.ProductId == c.ProductId).Sum(g => g.Count());
            if (count != c.Quantity) differences.Add(new(c.ProductId, c.ProductType, c.Name, c.Quantity, count));
        }
        foreach (var g in installed.Where(g => g.Key.ProductId is not { } id || contracted.All(c => c.ProductId != id)))
            differences.Add(new(g.Key.ProductId ?? Guid.Empty, g.Key.AssetType == AssetTypes.Panel ? Catalog.Constants.ProductTypes.SolarPanel : Catalog.Constants.ProductTypes.Inverter,
                g.First().Model, 0, g.Count()));
        if (differences.Count == 0) return new(EquipmentComparisonStatuses.Matches, [], fingerprint, null, null);

        // Only the stored, active decision for exactly this comparison counts (events are the audit trail, not the source).
        var active = await unitOfWork.GetActiveSubstitutionAsync(p.Id, false, cancellationToken);
        var current = active is not null && active.Fingerprint == fingerprint ? active : null;
        var manager = current is null ? null : new InstallationApprovalStep(current.ManagerUserId, current.ManagerReason, current.ManagerApprovedAt);
        var customer = current is { Status: EquipmentSubstitutionStatuses.Approved }
            ? new InstallationApprovalStep(current.CustomerUserId, null, current.CustomerApprovedAt!.Value) : null;
        var status = manager is null && p.Status == InstallationStatuses.Completed ? EquipmentComparisonStatuses.NotCheckedAtHandover
            : manager is null ? EquipmentComparisonStatuses.ApprovalRequired
            : customer is null ? EquipmentComparisonStatuses.CustomerApprovalRequired
            : EquipmentComparisonStatuses.Approved;
        return new(status, differences, fingerprint, manager, customer);
    }

    /// <summary>Ends the active substitution of the project (SUPERSEDED or CUSTOMER_DECLINED); the row is kept.</summary>
    private static void End(EquipmentSubstitution substitution, string status, DateTimeOffset now, Guid? customerUserId = null, string? reason = null)
    {
        substitution.Status = status;
        if (status == EquipmentSubstitutionStatuses.Superseded) substitution.SupersededAt = now;
        if (status == EquipmentSubstitutionStatuses.CustomerDeclined)
        {
            substitution.CustomerUserId = customerUserId;
            substitution.CustomerDeclinedAt = now;
            substitution.CustomerDeclineReason = reason;
        }
        substitution.UpdatedAt = now;
        substitution.Revision++;
    }

    /// <summary>Installation assets changed: an active approval of the previous panels/inverters no longer applies.</summary>
    private async Task SupersedeActiveSubstitutionAsync(Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await unitOfWork.GetActiveSubstitutionAsync(projectId, true, cancellationToken) is { } active)
            End(active, EquipmentSubstitutionStatuses.Superseded, now);
    }

    /// <summary>The customer asks for rework instead of agreeing to the pending substitution.</summary>
    private async Task DeclineActiveSubstitutionAsync(Guid projectId, Guid customerUserId, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await unitOfWork.GetActiveSubstitutionAsync(projectId, true, cancellationToken) is { Status: EquipmentSubstitutionStatuses.CustomerApprovalPending } active)
            End(active, EquipmentSubstitutionStatuses.CustomerDeclined, now, customerUserId, reason);
    }

    /// <summary>Stored substitution decisions of the project, oldest first (staff only).</summary>
    private async Task<IReadOnlyList<SubstitutionRecordView>> SubstitutionHistoryAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var records = await unitOfWork.ListSubstitutionsAsync(projectId, cancellationToken);
        if (records.Count == 0) return [];
        var items = (await unitOfWork.GetSubstitutionItemsAsync(records.Select(r => r.Id).ToList(), cancellationToken)).ToLookup(i => i.SubstitutionId);
        return records.Select(r => new SubstitutionRecordView(r.Id, r.Status, r.Fingerprint, r.QuotationId, r.ManagerUserId, r.ManagerReason, Local(r.ManagerApprovedAt)!.Value,
            Local(r.CustomerApprovedAt), Local(r.CustomerDeclinedAt), r.CustomerDeclineReason, Local(r.SupersededAt),
            items[r.Id].Select(i => new EquipmentDifference(i.ProductId ?? Guid.Empty, i.ProductType, i.Name, i.ContractedQuantity, i.InstalledQuantity)).ToList())).ToList();
    }

    private static string Fingerprint(IReadOnlyList<ContractedEquipment> contracted, IEnumerable<(string Product, string Type, int Count)> installed)
    {
        var s = new StringBuilder();
        foreach (var c in contracted.OrderBy(c => c.ProductId)) s.Append("c|").Append(c.ProductId).Append('|').Append(c.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var i in installed.OrderBy(i => i.Product, StringComparer.Ordinal).ThenBy(i => i.Type, StringComparer.Ordinal))
            s.Append("i|").Append(i.Product).Append('|').Append(i.Type).Append('|').Append(i.Count).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToString())), 0, 16).ToLowerInvariant();
    }
}
