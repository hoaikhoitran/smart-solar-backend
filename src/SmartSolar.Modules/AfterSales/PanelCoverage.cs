using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.AfterSales;

/// <summary>Units of one installed panel row to protect (a serialised panel: 1; a lot: 1..its confirmed quantity).</summary>
public sealed record AssetUnitsInput(Guid AssetId, int Units);

/// <summary>Panel units of one installed panel row protected by one term.</summary>
public sealed record CoverageAllocationView(Guid AllocationId, Guid AssetId, int UnitCount, string Manufacturer, string Model, string? SerialNumber, bool IsLot,
    string AssetStatus);

/// <summary>Per kind: active panel units now, units already held (active, paid, reserved or queued terms), units that can still be bought.</summary>
/// <param name="Blocker">Why no per-panel unit of this kind can be bought now (installation-wide term in force, bundled package not provisioned yet…).</param>
/// <param name="RenewableSubscriptionIds">PANEL-scope terms of this kind that can be renewed now (one queued renewal each).</param>
public sealed record KindCapacityView(string Kind, int ActivePanelUnits, int HeldUnits, int AvailableUnits, string? Blocker,
    IReadOnlyList<Guid> RenewableSubscriptionIds);

public sealed record PanelUnitsView(string Kind, int HeldUnits, int AvailableUnits);

public sealed record PanelAssetView(Guid AssetId, string Manufacturer, string Model, string? SerialNumber, bool IsLot, int Quantity, string? PositionCode,
    IReadOnlyList<PanelUnitsView> Coverage);

/// <summary>What the customer can buy per panel for one installation (computed by the backend; the FE never guesses the quantity).</summary>
public sealed record PanelEligibilityView(Guid ContractId, Guid? ProjectId, bool ContractFullyPaid, bool HandedOver, bool CanPurchase, string? Reason,
    IReadOnlyList<PanelAssetView> Panels, IReadOnlyList<KindCapacityView> Kinds, IReadOnlyList<PackageView> PerPanelPackages);

/// <summary>Staff view of a bundled package commitment and what it became.</summary>
public sealed record BundledPackageView(Guid CommitmentId, Guid ContractId, string Kind, string PackageName, decimal UnitPrice, int Quantity, decimal TotalAmount,
    decimal DepositAllocation, decimal BalanceAllocation, string Status, string? StatusReason, DateTimeOffset? StatusChangedAt, Guid? SubscriptionId,
    string? SubscriptionStatus, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateTimeOffset? CoverageStartsAt, int Revision);

/// <summary>
/// Overlap rules of PANEL-scope coverage (pure reads; the caller holds the contract lock). The units of one installed panel row held by terms of one kind
/// whose periods may overlap never exceed the row's quantity. A term holds its units while it is PAID / ACTIVE / CANCELLATION_REQUESTED and has not ended,
/// and reserves them while its online checkout is open or its cash choice is pending. An installation-wide (legacy) term of a kind in force excludes
/// per-panel units of that kind and vice versa.
/// </summary>
public static class PanelCoverageRules
{
    /// <summary>True when <paramref name="s"/> holds (or reserves) coverage that may overlap a term starting at <paramref name="from"/>.</summary>
    public static async Task<bool> HoldsAsync(IAfterSalesUnitOfWork data, ServiceSubscription s, DateTimeOffset from, DateTimeOffset now, CancellationToken token)
    {
        if (SubscriptionStatuses.HoldsTerm(s.Status)) return s.EndsAt is null || s.EndsAt > from;
        if (s.Status != SubscriptionStatuses.AwaitingPayment) return false;
        if (s.CheckoutExpiresAt > now) return true;
        return s.CurrentPaymentAttemptId is { } attemptId && await data.FindPaymentAttemptAsync(attemptId, token) is { Provider: PaymentProviders.Cash } cash
               && PaymentStatuses.Payable.Contains(cash.Status);
    }

    /// <summary>Units of each panel row held by PANEL-scope terms of <paramref name="kind"/> overlapping a term from <paramref name="from"/>.</summary>
    public static async Task<Dictionary<Guid, int>> HeldUnitsAsync(IAfterSalesUnitOfWork data, Guid contractId, string kind, DateTimeOffset from,
        IReadOnlyCollection<Guid> except, DateTimeOffset now, CancellationToken token)
    {
        var holding = new List<Guid>();
        foreach (var s in (await data.ListSubscriptionsForContractAsync(contractId, false, token))
                     .Where(s => s.Kind == kind && s.CoverageScope == CoverageScopes.Panel && !except.Contains(s.Id)))
            if (await HoldsAsync(data, s, from, now, token)) holding.Add(s.Id);
        return (await data.ListCoverageAllocationsAsync(holding, token)).GroupBy(a => a.InstalledAssetId).ToDictionary(g => g.Key, g => g.Sum(a => a.UnitCount));
    }

    /// <summary>An installation-wide (legacy) term of <paramref name="kind"/> in force or reserved for the contract, if any.</summary>
    public static async Task<ServiceSubscription?> InstallationWideAsync(IAfterSalesUnitOfWork data, Guid contractId, string kind, DateTimeOffset from,
        DateTimeOffset now, CancellationToken token)
    {
        foreach (var s in (await data.ListSubscriptionsForContractAsync(contractId, false, token))
                     .Where(s => s.Kind == kind && s.CoverageScope == CoverageScopes.Installation))
            if (await HoldsAsync(data, s, from, now, token)) return s;
        return null;
    }

    /// <summary>True when a PANEL-scope term of <paramref name="kind"/> holds or reserves coverage on the contract (blocks a legacy installation-wide purchase).</summary>
    public static async Task<bool> AnyPanelHoldingAsync(IAfterSalesUnitOfWork data, Guid contractId, string kind, DateTimeOffset now, CancellationToken token)
    {
        foreach (var s in (await data.ListSubscriptionsForContractAsync(contractId, false, token))
                     .Where(s => s.Kind == kind && s.CoverageScope == CoverageScopes.Panel))
            if (await HoldsAsync(data, s, now, now, token)) return true;
        return (await data.ListContractServicePackagesAsync(contractId, false, token))
            .Any(c => c.Kind == kind && c.Status is ContractServicePackageStatuses.Committed or ContractServicePackageStatuses.ReconciliationRequired);
    }

    /// <summary>
    /// Why the allocations of <paramref name="subscription"/> cannot be held (null = they fit): a panel row that is no longer an active panel, more units than
    /// the row has once the other overlapping terms are counted, or an installation-wide term of the same kind.
    /// </summary>
    public static async Task<string?> ConflictAsync(IAfterSalesUnitOfWork data, ServiceSubscription subscription, IReadOnlyCollection<ServiceCoverageAllocation> allocations,
        DateTimeOffset from, DateTimeOffset now, CancellationToken token)
    {
        if (await InstallationWideAsync(data, subscription.ContractId, subscription.Kind, from, now, token) is { } wide)
            return $"An installation-wide {subscription.Kind} term ({wide.PackageName}) is in force or reserved; per-panel coverage of the same kind would overlap it.";
        var except = new List<Guid> { subscription.Id };
        if (subscription.RenewsSubscriptionId is { } renewed) except.Add(renewed);
        var held = await HeldUnitsAsync(data, subscription.ContractId, subscription.Kind, from, except, now, token);
        foreach (var allocation in allocations)
        {
            var asset = await data.FindAssetAsync(allocation.InstalledAssetId, false, token);
            if (asset is null || asset.AssetType != AssetTypes.Panel || asset.Status != InstalledAssetStatuses.Active)
                return $"Panel {allocation.InstalledAssetId} is not an active installed panel any more.";
            var taken = held.GetValueOrDefault(asset.Id);
            if (taken + allocation.UnitCount > asset.Quantity)
                return $"Panel {Describe(asset)}: {allocation.UnitCount} unit(s) requested but only {Math.Max(0, asset.Quantity - taken)} of {asset.Quantity} are not already covered for this period.";
        }
        return null;
    }

    public static string Describe(InstalledAsset a) => a.SerialNumber is { } serial ? $"{a.Model} S/N {serial}" : $"{a.Model} (lot of {a.Quantity})";

    public static IEnumerable<InstalledAsset> ActivePanels(IEnumerable<InstalledAsset> assets)
        => assets.Where(a => a.AssetType == AssetTypes.Panel && a.Status == InstalledAssetStatuses.Active);
}

/// <summary>
/// Bundled per-panel packages after the contract is fully paid (2026-11-25), panel eligibility for retail purchases and the staff reconciliation view.
/// <list type="bullet">
/// <item>Provisioning needs: contract PAID (both installments verified), installation handed over, and the ACTIVE installed panel units (serialised panels and
/// lots, no invented serial) equal to the contracted quantity. Then ONE PANEL-scope subscription per commitment (unique index), allocated to the real panel
/// rows, PAID at the full-payment time; its term starts at the later of full payment and handover (disaster waiting period applies) via
/// <see cref="SubscriptionTerms"/>. Maintenance visits are the package's plan months — never multiplied by panels; disaster limits are not multiplied.</item>
/// <item>A mismatch (e.g. 10 contracted, 9 installed) or a conflicting coverage → RECONCILIATION_REQUIRED: nothing is granted, no amount changes, Managers
/// are notified; after the asset record is corrected a Manager/Admin retries.</item>
/// </list>
/// </summary>
public sealed class PanelCoverageService(IAfterSalesUnitOfWork data, SubscriptionTerms terms, TimeProvider time) : IBundledPackageProvisioner
{
    /// <summary>Inside the payment confirmation transaction (contract just became PAID). Never throws for business conditions.</summary>
    public async Task ContractFullyPaidAsync(Guid contractId, DateTimeOffset paidAt, DateTimeOffset now, CancellationToken cancellationToken)
        => await ProvisionAsync(contractId, paidAt, null, now, cancellationToken, paidInThisTransaction: true);

    /// <summary>Provisions every pending commitment of the contract (caller holds a transaction). Returns how many were provisioned.</summary>
    /// <param name="paidInThisTransaction">The ledger made the contract PAID in the current (not yet saved) transaction: the stored status still shows the
    /// previous one, so the caller's word is used instead of the stored status.</param>
    private async Task<int> ProvisionAsync(Guid contractId, DateTimeOffset? paidAt, Guid? actorUserId, DateTimeOffset now, CancellationToken token,
        bool paidInThisTransaction = false)
    {
        var pending = (await data.ListContractServicePackagesAsync(contractId, true, token))
            .Where(c => c.Status is ContractServicePackageStatuses.Committed or ContractServicePackageStatuses.ReconciliationRequired).ToList();
        if (pending.Count == 0) return 0;
        await data.LockContractAsync(contractId, token);
        var contract = await data.GetContractAsync(contractId, token);
        // The full-payment time: the caller's (same transaction) or the stored one (retry).
        var fullyPaidAt = paidAt ?? contract?.PaidAt;
        if (contract is null || (!paidInThisTransaction && contract.Status != ContractStatuses.Paid) || fullyPaidAt is null) return 0;   // not fully paid: nothing is due yet
        if (contract.ProjectId is not { } projectId || contract.HandoverAt is null)
        {
            foreach (var c in pending) await MarkReviewAsync(c, "The installation is not handed over yet; coverage is provisioned after the handover.", actorUserId, now, token);
            return 0;
        }

        var panels = PanelCoverageRules.ActivePanels(await data.ListAssetsAsync(projectId, false, token)).ToList();
        var installedUnits = panels.Sum(a => a.Quantity);
        var provisioned = 0;
        foreach (var commitment in pending)
        {
            if (installedUnits != commitment.Quantity)
            {
                await MarkReviewAsync(commitment, $"Contracted {commitment.Quantity} panel unit(s) but {installedUnits} active panel unit(s) are registered on the installation; "
                    + "correct the installed equipment record (or decide the difference) and retry. No coverage was granted and no amount was changed.", actorUserId, now, token);
                continue;
            }
            var subscription = new ServiceSubscription
            {
                Id = Guid.NewGuid(), CustomerUserId = contract.CustomerUserId, ContractId = contractId, ProjectId = projectId, PackageId = commitment.PackageId,
                Kind = commitment.Kind, Status = SubscriptionStatuses.Paid, PackageName = commitment.PackageName, Price = commitment.TotalAmount,
                DurationMonths = commitment.DurationMonths, VisitMonths = commitment.VisitMonths, CoveredDisasterTypes = commitment.CoveredDisasterTypes,
                CoveragePercent = commitment.CoveragePercent, PerIncidentLimit = commitment.PerIncidentLimit, TermLimit = commitment.TermLimit,
                CoverageScope = CoverageScopes.Panel, UnitPrice = commitment.UnitPrice, CoveredUnits = commitment.Quantity, ContractServicePackageId = commitment.Id,
                PaidAt = fullyPaidAt, CreatedAt = now, UpdatedAt = now,
            };
            var allocations = panels.Select(a => new ServiceCoverageAllocation
            {
                Id = Guid.NewGuid(), SubscriptionId = subscription.Id, ContractId = contractId, InstalledAssetId = a.Id, Kind = commitment.Kind, UnitCount = a.Quantity,
                CreatedAt = now,
            }).ToList();
            if (await PanelCoverageRules.ConflictAsync(data, subscription, allocations, now, now, token) is { } conflict)
            {
                await MarkReviewAsync(commitment, $"{conflict} No coverage was granted and no amount was changed.", actorUserId, now, token);
                continue;
            }
            data.Add(subscription);
            foreach (var allocation in allocations) data.Add(allocation);
            commitment.Status = ContractServicePackageStatuses.Provisioned;
            commitment.SubscriptionId = subscription.Id;
            commitment.StatusReason = null;
            commitment.StatusChangedAt = now;
            commitment.UpdatedAt = now;
            commitment.Revision++;
            AfterSalesHistory.Record(data, EventSubjects.BundledPackage, commitment.Id, "PROVISIONED", actorUserId, $"subscription {subscription.Id}", now);
            AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "BUNDLED_PROVISIONED", actorUserId,
                $"{commitment.PackageName}: {commitment.UnitPrice:0} VND × {commitment.Quantity} panels = {commitment.TotalAmount:0} VND paid in the contract's 30/70 installments; "
                + $"allocated to {allocations.Count} panel row(s) ({string.Join(", ", panels.Select(PanelCoverageRules.Describe))})", now);
            AfterSalesHistory.Notify(data, contract.CustomerUserId, "PACKAGE_PAID",
                $"{commitment.PackageName} ({commitment.Quantity} tấm pin) đã được ghi nhận theo hợp đồng.", now, subscriptionId: subscription.Id);
            // Starts (later of full payment and handover; disaster waiting period) and activates when due; otherwise the sweep does it.
            await terms.StartTermIfPossibleAsync(subscription, now, token);
            provisioned++;
        }
        return provisioned;
    }

    private async Task MarkReviewAsync(ContractServicePackage commitment, string reason, Guid? actorUserId, DateTimeOffset now, CancellationToken token)
    {
        reason = reason.Length <= 1000 ? reason : reason[..1000];
        if (commitment.Status == ContractServicePackageStatuses.ReconciliationRequired && commitment.StatusReason == reason) return;   // unchanged: no duplicate audit
        commitment.Status = ContractServicePackageStatuses.ReconciliationRequired;
        commitment.StatusReason = reason;
        commitment.StatusChangedAt = now;
        commitment.UpdatedAt = now;
        commitment.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.BundledPackage, commitment.Id, "RECONCILIATION_REQUIRED", actorUserId, reason, now);
        AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token), "PACKAGE_RECONCILIATION",
            $"Gói {commitment.PackageName} của hợp đồng cần đối chiếu số tấm pin: {reason}", now);
    }

    // ================================================================== staff

    /// <summary>Manager/Admin: bundled package commitments, newest first (optionally one status).</summary>
    public async Task<Common.Paging.PagedResult<BundledPackageView>> PageBundledAsync(string? status, Common.Paging.PageRequest page, CancellationToken cancellationToken)
    {
        var (rows, total) = await data.PageContractServicePackagesAsync(string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), page.Skip, page.PageSize,
            cancellationToken);
        var list = new List<BundledPackageView>();
        foreach (var c in rows) list.Add(await BundledViewAsync(c, cancellationToken));
        return Common.Paging.PagedResult<BundledPackageView>.Create(list, page.Page, page.PageSize, total);
    }

    /// <summary>
    /// Manager/Admin: retries the provisioning of a commitment waiting for reconciliation (after the installed panel record was corrected). Nothing is
    /// granted unless every rule holds; nothing financial ever changes.
    /// </summary>
    public async Task<OperationResult<BundledPackageView>> RetryReconciliationAsync(Guid staffUserId, Guid commitmentId, CancellationToken cancellationToken)
    {
        var probe = await data.FindContractServicePackageAsync(commitmentId, false, cancellationToken);
        if (probe is null) return OperationResult<BundledPackageView>.Fail(AfterSalesErrorCodes.BundledPackageNotFound, "Bundled package was not found.");
        try
        {
            var outcome = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await data.LockContractAsync(probe.ContractId, token);
                var commitment = await data.FindContractServicePackageAsync(commitmentId, true, token);
                if (commitment?.Status != ContractServicePackageStatuses.ReconciliationRequired)
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, $"The bundled package is {commitment?.Status}; only one waiting for reconciliation can be retried."), false);
                var now = time.GetUtcNow();
                await ProvisionAsync(probe.ContractId, null, staffUserId, now, token);
                await data.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<BundledPackageView>();
        }
        catch (Common.Persistence.PersistenceConflictException)
        {
            return OperationResult<BundledPackageView>.Fail(AfterSalesErrorCodes.StaleRevision, "The bundled package changed meanwhile. Reload and try again.");
        }
        return OperationResult<BundledPackageView>.Ok(await BundledViewAsync((await data.FindContractServicePackageAsync(commitmentId, false, cancellationToken))!,
            cancellationToken));
    }

    private async Task<BundledPackageView> BundledViewAsync(ContractServicePackage c, CancellationToken token)
    {
        var s = c.SubscriptionId is { } id ? await data.FindSubscriptionAsync(id, false, token) : null;
        return new BundledPackageView(c.Id, c.ContractId, c.Kind, c.PackageName, c.UnitPrice, c.Quantity, c.TotalAmount, c.DepositAllocation, c.BalanceAllocation,
            c.Status, c.StatusReason, c.StatusChangedAt, c.SubscriptionId, s?.Status, s?.StartsAt, s?.EndsAt, s?.CoverageStartsAt, c.Revision);
    }

    // ================================================================== customer eligibility

    /// <summary>Per-panel purchase capacity of an owned installation (read-only).</summary>
    public async Task<OperationResult<PanelEligibilityView>> EligibilityAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await data.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PanelEligibilityView>.Fail(AfterSalesErrorCodes.ProjectNotFound, "Contract was not found.");
        var now = time.GetUtcNow();
        var packages = (await data.ListPackagesAsync(null, true, cancellationToken)).Where(p => p.PricingUnit == PackagePricingUnits.PerPanel)
            .Select(AfterSalesViews.Package).ToList();
        var paid = contract.Status == ContractStatuses.Paid;
        var handedOver = contract.ProjectId is not null && contract.HandoverAt is not null;
        var reason = !paid ? "The installation contract must be fully paid first."
            : !handedOver ? "Per-panel packages are bought after the installation is handed over."
            : contract.ProjectStatus == InstallationStatuses.Cancelled ? "The installation was cancelled."
            : null;
        if (reason is not null)
            return OperationResult<PanelEligibilityView>.Ok(new PanelEligibilityView(contractId, contract.ProjectId, paid, handedOver, false, reason, [], [], packages));

        var panels = PanelCoverageRules.ActivePanels(await data.ListAssetsAsync(contract.ProjectId!.Value, false, cancellationToken)).ToList();
        var subscriptions = await data.ListSubscriptionsForContractAsync(contractId, false, cancellationToken);
        var commitments = await data.ListContractServicePackagesAsync(contractId, false, cancellationToken);
        var held = new Dictionary<string, Dictionary<Guid, int>>();
        var kinds = new List<KindCapacityView>();
        foreach (var kind in ServicePackageKinds.All)
        {
            held[kind] = await PanelCoverageRules.HeldUnitsAsync(data, contractId, kind, now, [], now, cancellationToken);
            var wide = await PanelCoverageRules.InstallationWideAsync(data, contractId, kind, now, now, cancellationToken);
            var bundledPending = commitments.Any(c => c.Kind == kind && c.Status is ContractServicePackageStatuses.Committed or ContractServicePackageStatuses.ReconciliationRequired);
            var units = panels.Sum(a => a.Quantity);
            var heldUnits = panels.Sum(a => Math.Min(a.Quantity, held[kind].GetValueOrDefault(a.Id)));
            var blocker = wide is not null ? $"An installation-wide {kind} term ({wide.PackageName}) is in force."
                : bundledPending ? $"The {kind} package of the contract is not provisioned yet (reconciliation pending)."
                : null;
            var renewable = new List<Guid>();
            foreach (var s in subscriptions.Where(s => s.Kind == kind && s.CoverageScope == CoverageScopes.Panel && SubscriptionStatuses.HoldsTerm(s.Status)
                                                       && s.EndsAt is not null && s.EndsAt > now))
                if (!subscriptions.Any(r => r.RenewsSubscriptionId == s.Id && r.Status is not (SubscriptionStatuses.Cancelled or SubscriptionStatuses.CheckoutExpired)))
                    renewable.Add(s.Id);
            kinds.Add(new KindCapacityView(kind, units, heldUnits, blocker is null ? units - heldUnits : 0, blocker, blocker is null ? renewable : []));
        }
        var assetViews = panels.Select(a => new PanelAssetView(a.Id, a.Manufacturer, a.Model, a.SerialNumber, a.SerialNumber is null, a.Quantity, a.PositionCode,
            ServicePackageKinds.All.Select(k =>
            {
                var taken = Math.Min(a.Quantity, held[k].GetValueOrDefault(a.Id));
                return new PanelUnitsView(k, taken, kinds.First(x => x.Kind == k).Blocker is null ? a.Quantity - taken : 0);
            }).ToList())).ToList();
        return OperationResult<PanelEligibilityView>.Ok(new PanelEligibilityView(contractId, contract.ProjectId, paid, handedOver,
            kinds.Any(k => k.AvailableUnits > 0 || k.RenewableSubscriptionIds.Count > 0), panels.Count == 0 ? "No active installed panel is registered." : null,
            assetViews, kinds, packages));
    }
}
