using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

/// <param name="ImageUrl">Cover picture: absolute https URL (the public address of an uploaded PACKAGE_IMAGE file, or a hosted picture); null removes it.</param>
/// <param name="PricingUnit">PER_PANEL or PER_INSTALLATION_LEGACY. Create: omitted = PER_INSTALLATION_LEGACY (the historical meaning). Update: omitted = unchanged;
/// it cannot be changed (create a new package instead), so an existing price is never reinterpreted.</param>
public sealed record PackageInput(string? Kind, string? Name, string? Description, decimal Price, int DurationMonths, IReadOnlyList<int>? VisitMonths,
    IReadOnlyList<string>? CoveredDisasterTypes, decimal? CoveragePercent, decimal? PerIncidentLimit, decimal? TermLimit, bool IsActive, string? ImageUrl = null,
    string? PricingUnit = null);

/// <summary>
/// Packages to buy for one installation contract. When <see cref="IncludeContract"/> is true the SIGNED contract is paid in the same
/// PayOS checkout (its amount stays a separate line; the contract total itself never changes).
/// </summary>
/// <param name="PaymentMethod">ONLINE (PayOS, default) or CASH (a pending cash attempt confirmed by a Manager/Admin): with the SIGNED contract
/// (one cash payment for the installation and the packages) or, for a PAID contract, the packages alone.</param>
/// <param name="Quantity">PER_PANEL packages: number of panel units (optional; when given it must equal Σ <paramref name="TargetAssets"/> units).</param>
/// <param name="TargetAssets">PER_PANEL packages: the active installed panel rows and units to protect (from the eligibility endpoint). Not allowed for
/// PER_INSTALLATION_LEGACY packages.</param>
/// <param name="RenewsSubscriptionId">PER_PANEL renewal of one of the customer's PANEL-scope terms (one package only): the new term starts when it ends
/// (continuous renewal, no new waiting period); targets default to its still-active panels.</param>
public sealed record PurchaseInput(Guid ContractId, Guid? MaintenancePackageId, Guid? DisasterPackageId, bool IncludeContract, string? PaymentMethod = null,
    int? Quantity = null, IReadOnlyList<AssetUnitsInput>? TargetAssets = null, Guid? RenewsSubscriptionId = null);

public sealed record PurchaseView(PaymentAttemptView Checkout, IReadOnlyList<Guid> SubscriptionIds);

/// <summary>
/// When a purchased term starts and ends. A term starts once its payment is verified AND the installation is handed over: at the later
/// of the two, or — for a renewal bought before the current term ends — exactly when the current term ends (continuous renewal, no new
/// disaster waiting period). A disaster-protection term starts covering incidents after the waiting period (inside the term).
/// </summary>
public sealed class SubscriptionTerms(IAfterSalesUnitOfWork data, ISchedulingPolicyProvider policies, WorkingCalendar calendar)
{
    /// <summary>Fixes the term of a PAID subscription when the installation is handed over (no-op otherwise). Call inside a transaction.</summary>
    public async Task StartTermIfPossibleAsync(ServiceSubscription subscription, DateTimeOffset now, CancellationToken token)
    {
        if (subscription.StartsAt is not null || subscription.PaidAt is not { } paidAt) return;
        var contract = await data.GetContractAsync(subscription.ContractId, token);
        if (contract?.ProjectId is not { } projectId || contract.HandoverAt is not { } handoverAt) return;

        await data.LockContractAsync(subscription.ContractId, token);
        // Installation-wide terms renew the previous installation-wide term of the kind; a PANEL-scope term renews only the term named at purchase
        // (the same panels), never another scope's term.
        var previous = subscription.CoverageScope == CoverageScopes.Panel
            ? subscription.RenewsSubscriptionId is { } renewedId && await data.FindSubscriptionAsync(renewedId, false, token) is { EndsAt: not null } renewed
              && (SubscriptionStatuses.HoldsTerm(renewed.Status) || renewed.Status == SubscriptionStatuses.Expired) ? renewed : null
            : (await data.ListSubscriptionsForContractAsync(subscription.ContractId, true, token))
                .Where(s => s.Id != subscription.Id && s.Kind == subscription.Kind && s.CoverageScope == CoverageScopes.Installation && s.EndsAt is not null
                            && (SubscriptionStatuses.HoldsTerm(s.Status) || s.Status == SubscriptionStatuses.Expired))
                .MaxBy(s => s.EndsAt);
        var earliest = paidAt > handoverAt ? paidAt : handoverAt;
        var continuous = previous is not null && previous.EndsAt >= earliest;
        var start = continuous ? previous!.EndsAt!.Value : earliest;

        subscription.ProjectId = projectId;
        subscription.StartsAt = start;
        subscription.EndsAt = AfterSalesRules.AddMonths(start, subscription.DurationMonths, calendar.TimeZone);
        subscription.RenewsSubscriptionId = continuous ? previous!.Id : null;
        if (subscription.Kind == ServicePackageKinds.DisasterProtection)
        {
            var waitingDays = (await policies.GetAsync(token)).Get(AfterSalesSettingKeys.DisasterWaitingDays);
            subscription.CoverageStartsAt = continuous ? start : start.AddDays(waitingDays);
        }
        else
        {
            foreach (var month in AfterSalesRules.ParseMonths(subscription.VisitMonths))
            {
                var (periodStart, periodEnd) = AfterSalesRules.MaintenancePeriod(start, month, calendar.TimeZone);
                data.Add(new MaintenanceEntitlement
                {
                    Id = Guid.NewGuid(), SubscriptionId = subscription.Id, PlanMonth = month, PeriodStartsAt = periodStart, PeriodEndsAt = periodEnd,
                    Status = EntitlementStatuses.Available, UpdatedAt = now,
                });
            }
        }
        subscription.UpdatedAt = now;
        subscription.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "TERM_FIXED", null,
            $"{start:O} – {subscription.EndsAt:O}{(continuous ? " (continuous renewal)" : "")}", now);
        ActivateIfDue(subscription, now);
    }

    /// <summary>PAID → ACTIVE once the term has started.</summary>
    public void ActivateIfDue(ServiceSubscription subscription, DateTimeOffset now)
    {
        if (subscription.Status != SubscriptionStatuses.Paid || subscription.StartsAt is not { } start || start > now) return;
        subscription.Status = SubscriptionStatuses.Active;
        subscription.ActivatedAt = now;
        subscription.UpdatedAt = now;
        subscription.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "ACTIVATED", null, null, now);
        AfterSalesHistory.Notify(data, subscription.CustomerUserId, "PACKAGE_ACTIVE", $"Your {subscription.PackageName} is now active.", now, subscriptionId: subscription.Id);
    }
}

/// <summary>
/// Service packages: Admin catalog and warranty duration options (changes affect future purchases only), customer purchases (standalone
/// or combined with the contract payment), and cancellation of UNPAID orders. A paid package is not cancelled online or by a Manager: that
/// would owe the customer money and refunds are not handled in this system.
/// </summary>
public sealed class ServicePackageHandlers(
    IAfterSalesUnitOfWork data,
    PaymentService payments,
    ISchedulingPolicyProvider policies,
    AfterSalesViews views,
    TimeProvider time,
    Catalog.ProductImages.CatalogOptions? catalog = null)
{
    /// <summary>Package covers follow the product image URL rules (https, no credentials, optional host allow-list).</summary>
    private readonly Catalog.ProductImages.CatalogOptions _catalog = catalog ?? new Catalog.ProductImages.CatalogOptions();

    // ================================================================== Admin: catalog

    public async Task<IReadOnlyList<PackageView>> ListPackagesAsync(string? kind, bool activeOnly, CancellationToken cancellationToken)
        => (await data.ListPackagesAsync(kind, activeOnly, cancellationToken)).Select(AfterSalesViews.Package).ToList();

    /// <summary>
    /// Technician (on-site sale): the packages that can be bundled with an installation quotation — active and priced PER_PANEL only (legacy
    /// per-installation packages are never offered per panel). Read-only; the price is the Admin catalog's.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<PackageView>>> ListSellablePerPanelAsync(string? kind, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToUpperInvariant();
        if (normalized is not null && !ServicePackageKinds.All.Contains(normalized))
            return OperationResult<IReadOnlyList<PackageView>>.Fail(AfterSalesErrorCodes.ValidationFailed, $"kind must be one of {string.Join(", ", ServicePackageKinds.All)}.");
        return OperationResult<IReadOnlyList<PackageView>>.Ok((await data.ListPackagesAsync(normalized, true, cancellationToken))
            .Where(p => p.PricingUnit == PackagePricingUnits.PerPanel).Select(AfterSalesViews.Package).ToList());
    }

    public async Task<OperationResult<PackageView>> CreatePackageAsync(Guid adminUserId, PackageInput input, CancellationToken cancellationToken)
    {
        if (ValidatePackage(input, _catalog) is { } error) return OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, error);
        var now = time.GetUtcNow();
        var package = new ServicePackage
        {
            Id = Guid.NewGuid(), Kind = input.Kind!, CreatedAt = now,
            PricingUnit = string.IsNullOrWhiteSpace(input.PricingUnit) ? PackagePricingUnits.PerInstallationLegacy : input.PricingUnit.Trim().ToUpperInvariant(),
        };
        Apply(package, input, now);
        await data.ExecuteInTransactionAsync<bool>(async token =>
        {
            data.Add(package);
            AfterSalesHistory.Record(data, EventSubjects.Package, package.Id, "PACKAGE_CREATED", adminUserId, Describe(package), now);
            await data.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        return OperationResult<PackageView>.Ok(AfterSalesViews.Package(package));
    }

    /// <summary>Edits a package (kind is fixed). Existing purchases keep their own snapshot; only future purchases see the change.</summary>
    public async Task<OperationResult<PackageView>> UpdatePackageAsync(Guid adminUserId, Guid packageId, PackageInput input, int expectedRevision, CancellationToken cancellationToken)
    {
        if (ValidatePackage(input, _catalog) is { } error) return OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, error);
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<PackageView>>(async token =>
            {
                var package = await data.FindPackageAsync(packageId, true, token);
                if (package is null) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.PackageNotFound, "Package was not found."), false);
                if (package.Revision != expectedRevision) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.StaleRevision, "The package changed meanwhile."), false);
                if (package.Kind != input.Kind) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, "The package kind cannot change."), false);
                if (!string.IsNullOrWhiteSpace(input.PricingUnit) && input.PricingUnit.Trim().ToUpperInvariant() != package.PricingUnit)
                    return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed,
                        "The pricing unit of a package cannot change (its price would be reinterpreted); create a new package instead."), false);
                var now = time.GetUtcNow();
                var before = Describe(package);
                Apply(package, input, now);
                package.Revision++;
                AfterSalesHistory.Record(data, EventSubjects.Package, package.Id, "PACKAGE_UPDATED", adminUserId, $"before: {before}; after: {Describe(package)}", now);
                await data.SaveChangesAsync(token);
                return (OperationResult<PackageView>.Ok(AfterSalesViews.Package(package)), true);
            }, cancellationToken);
            return result;
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<PackageView>.Fail(AfterSalesErrorCodes.StaleRevision, "The package changed meanwhile.");
        }
    }

    private static string? ValidatePackage(PackageInput input, Catalog.ProductImages.CatalogOptions catalog)
    {
        if (!ServicePackageKinds.All.Contains(input.Kind ?? "")) return $"Kind must be one of {string.Join(", ", ServicePackageKinds.All)}.";
        if (!string.IsNullOrWhiteSpace(input.PricingUnit) && !PackagePricingUnits.All.Contains(input.PricingUnit.Trim().ToUpperInvariant()))
            return $"pricingUnit must be one of {string.Join(", ", PackagePricingUnits.All)}.";
        if (Text.Trim(input.Name, 150) is null) return "A name is required.";
        if (!string.IsNullOrWhiteSpace(input.ImageUrl) && Catalog.ProductImages.ProductImageRules.UrlError(input.ImageUrl, catalog) is { } imageError)
            return $"Cover image: {imageError}";
        if (input.Price <= 0 || decimal.Truncate(input.Price) != input.Price) return "The price is a whole VND amount above 0.";
        if (input.DurationMonths is < 1 or > 120) return "The duration is between 1 and 120 months.";
        if (input.Kind == ServicePackageKinds.Maintenance)
        {
            var months = input.VisitMonths ?? [];
            if (months.Count == 0 || months.Distinct().Count() != months.Count || months.Any(m => m < 1 || m > input.DurationMonths))
                return "Give the plan months of the visits (distinct, within the duration).";
        }
        else
        {
            var types = input.CoveredDisasterTypes ?? [];
            if (types.Count == 0 || types.Any(t => !DisasterTypes.All.Contains(t))) return $"Cover at least one of {string.Join(", ", DisasterTypes.All)}.";
            if (input.CoveragePercent is not (> 0 and <= 100)) return "The coverage percentage is above 0 and at most 100.";
            if (input.PerIncidentLimit is not (> 0) || input.TermLimit is not { } termLimit || termLimit < input.PerIncidentLimit) return "Limits: per incident > 0 and term limit ≥ per incident.";
            if (decimal.Truncate(input.PerIncidentLimit.Value) != input.PerIncidentLimit.Value || decimal.Truncate(termLimit) != termLimit) return "Limits are whole VND amounts.";
        }
        return null;
    }

    private static void Apply(ServicePackage package, PackageInput input, DateTimeOffset now)
    {
        var maintenance = input.Kind == ServicePackageKinds.Maintenance;
        package.Name = Text.Trim(input.Name, 150)!;
        package.Description = Text.Trim(input.Description, 2000);
        package.Price = input.Price;
        package.DurationMonths = input.DurationMonths;
        package.VisitMonths = maintenance ? string.Join(",", input.VisitMonths!.Order()) : null;
        package.CoveredDisasterTypes = maintenance ? null : string.Join(",", input.CoveredDisasterTypes!.Distinct().Order());
        package.CoveragePercent = maintenance ? null : input.CoveragePercent;
        package.PerIncidentLimit = maintenance ? null : input.PerIncidentLimit;
        package.TermLimit = maintenance ? null : input.TermLimit;
        package.ImageUrl = string.IsNullOrWhiteSpace(input.ImageUrl) ? null : Catalog.ProductImages.ProductImageRules.NormalizeUrl(input.ImageUrl);
        package.IsActive = input.IsActive;
        package.UpdatedAt = now;
    }

    private static string Describe(ServicePackage p)
        => $"{p.Name}; {p.Price:0} VND {p.PricingUnit}; {p.DurationMonths} months; visits {p.VisitMonths}; types {p.CoveredDisasterTypes}; {p.CoveragePercent}% ; "
           + $"per incident {p.PerIncidentLimit:0}; term {p.TermLimit:0}; image {(p.ImageUrl is null ? "none" : "set")}; active {p.IsActive}";

    // ================================================================== Admin: warranty durations

    public async Task<IReadOnlyList<WarrantyOptionView>> ListWarrantyOptionsAsync(string? kind, CancellationToken cancellationToken)
        => (await data.ListWarrantyOptionsAsync(kind, false, cancellationToken)).Select(AfterSalesViews.Option).ToList();

    /// <summary>Adds or updates the duration option <paramref name="months"/> of a kind; at most one default per kind.</summary>
    public async Task<OperationResult<WarrantyOptionView>> SaveWarrantyOptionAsync(Guid adminUserId, string? kind, int months, bool isActive, bool isDefault,
        CancellationToken cancellationToken)
    {
        if (!WarrantyOptionKinds.All.Contains(kind ?? "")) return OperationResult<WarrantyOptionView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Unknown warranty kind.");
        if (months is < 1 or > 240) return OperationResult<WarrantyOptionView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Months are between 1 and 240.");
        if (isDefault && !isActive) return OperationResult<WarrantyOptionView>.Fail(AfterSalesErrorCodes.ValidationFailed, "The default option must be active.");
        var result = await data.ExecuteInTransactionAsync<WarrantyOption>(async token =>
        {
            var now = time.GetUtcNow();
            var options = await data.ListWarrantyOptionsAsync(kind, true, token);
            if (isDefault)
            {
                foreach (var other in options.Where(o => o.IsDefault && o.Months != months)) other.IsDefault = false;
                await data.SaveChangesAsync(token);   // one default per kind (partial unique index)
            }
            var option = options.FirstOrDefault(o => o.Months == months);
            if (option is null)
            {
                option = new WarrantyOption { Id = Guid.NewGuid(), Kind = kind!, Months = months, CreatedAt = now };
                data.Add(option);
            }
            option.IsActive = isActive;
            option.IsDefault = isDefault;
            option.UpdatedAt = now;
            AfterSalesHistory.Record(data, EventSubjects.WarrantyOption, option.Id, "WARRANTY_OPTION_SAVED", adminUserId,
                $"{kind} {months} months; active {isActive}; default {isDefault}", now);
            await data.SaveChangesAsync(token);
            return (option, true);
        }, cancellationToken);
        return OperationResult<WarrantyOptionView>.Ok(AfterSalesViews.Option(result));
    }

    // ================================================================== Customer: purchase

    /// <summary>
    /// Buys packages for an installation contract and returns the PayOS checkout. The price and benefits are frozen now; nothing is granted
    /// until the payment is verified. A second term of a kind may be bought only as the next (non-overlapping) renewal, one at a time.
    /// </summary>
    public async Task<OperationResult<PurchaseView>> PurchaseAsync(Guid customerUserId, PurchaseInput input, CancellationToken cancellationToken)
    {
        var method = string.IsNullOrWhiteSpace(input.PaymentMethod) ? PaymentMethods.Online : input.PaymentMethod.Trim().ToUpperInvariant();
        if (method is not (PaymentMethods.Online or PaymentMethods.Cash))
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.ValidationFailed, "'paymentMethod' must be ONLINE or CASH.");
        var contract = await data.GetContractAsync(input.ContractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.ProjectNotFound, "Contract was not found.");
        // A 30/70 contract is paid per installment (CONTRACT_INSTALLMENT); its balance is never bundled with packages. After-sales stays 100% upfront.
        if (input.IncludeContract && contract.PaymentPlan is not null)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.InvalidState,
                "This contract is paid in two installments; pay its installment first, then buy the packages separately once it is fully paid.");
        if (input.IncludeContract && contract.Status != ContractStatuses.Signed)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.InvalidState, "The contract can be paid together only while it is signed and unpaid.");
        if (!input.IncludeContract && contract.Status != ContractStatuses.Paid)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.InvalidState, "Pay the installation contract first, or buy the packages together with it.");
        if (contract.ProjectStatus == InstallationStatuses.Cancelled)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.InvalidState, "The installation was cancelled.");

        var packages = new List<ServicePackage>();
        foreach (var (id, kind) in new[] { (input.MaintenancePackageId, ServicePackageKinds.Maintenance), (input.DisasterPackageId, ServicePackageKinds.DisasterProtection) })
        {
            if (id is null) continue;
            var package = await data.FindPackageAsync(id.Value, false, cancellationToken);
            if (package is null || package.Kind != kind) return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.PackageNotFound, "Package was not found.");
            if (!package.IsActive) return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.PackageInactive, $"{package.Name} is not sold any more.");
            packages.Add(package);
        }
        if (packages.Count == 0) return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Choose at least one package.");

        var now = time.GetUtcNow();
        var perPanel = packages.Where(p => p.PricingUnit == PackagePricingUnits.PerPanel).ToList();
        var panelPlan = new Dictionary<Guid, (List<ServiceCoverageAllocation> Allocations, int Units, Guid? Renews)>();
        if (perPanel.Count == 0 && (input.Quantity is not null || input.TargetAssets is not null || input.RenewsSubscriptionId is not null))
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.PricingUnitMismatch,
                "quantity, targetAssets and renewsSubscriptionId apply only to PER_PANEL packages; this package covers the whole installation.");
        if (perPanel.Count > 0)
        {
            if (perPanel.Count != packages.Count)
                return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.PricingUnitMismatch,
                    "Buy per-panel and installation-wide packages in separate purchases.");
            var plan = await PlanPanelPurchaseAsync(contract, perPanel, input, now, cancellationToken);
            if (!plan.Succeeded) return plan.As<PurchaseView>();
            panelPlan = plan.Value!;
        }

        var existing = await data.ListSubscriptionsForContractAsync(input.ContractId, false, cancellationToken);
        foreach (var package in packages.Where(p => p.PricingUnit != PackagePricingUnits.PerPanel))
        {
            // Installation-wide (legacy) term vs per-panel coverage of the same kind: one would overlap the other on the same panels.
            if (await PanelCoverageRules.AnyPanelHoldingAsync(data, input.ContractId, package.Kind, now, cancellationToken))
                return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.TermOverlap,
                    $"Per-panel {package.Kind} coverage of this installation is in force or pending; an installation-wide {package.Kind} package would overlap it.");
            existing = existing.Where(s => s.CoverageScope == CoverageScopes.Installation).ToList();
            // No mid-term upgrade and no second queued renewal: one future term per kind may wait for its turn.
            if (existing.Any(s => s.Kind == package.Kind && s.Status is SubscriptionStatuses.Paid or SubscriptionStatuses.CancellationRequested
                                   && (s.StartsAt is null || s.StartsAt > now)))
                return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.TermOverlap, "A renewal of this kind is already purchased.");
            if (existing.Any(s => s.Kind == package.Kind && s.Status == SubscriptionStatuses.AwaitingPayment && s.CheckoutExpiresAt > now))
                return OperationResult<PurchaseView>.Fail(PaymentErrorCodes.CheckoutInProgress, "A payment for this kind of package is already in progress.");
            // A pending cash choice has no online deadline: it blocks a second order of the same kind until it is confirmed or withdrawn.
            foreach (var waiting in existing.Where(s => s.Kind == package.Kind && s.Status == SubscriptionStatuses.AwaitingPayment && s.CurrentPaymentAttemptId is not null))
                if (await data.FindPaymentAttemptAsync(waiting.CurrentPaymentAttemptId!.Value, cancellationToken) is { Provider: PaymentProviders.Cash } cash
                    && PaymentStatuses.Payable.Contains(cash.Status))
                    return OperationResult<PurchaseView>.Fail(PaymentErrorCodes.CashSelected, "Cash payment was already chosen for this kind of package.");
        }

        var expiresAt = now.AddMinutes((await policies.GetAsync(cancellationToken)).Get(AfterSalesSettingKeys.CheckoutExpiryMinutes));
        var subscriptions = packages.Select(p =>
        {
            var panel = panelPlan.TryGetValue(p.Id, out var planned);
            return new ServiceSubscription
            {
                Id = Guid.NewGuid(), CustomerUserId = customerUserId, ContractId = input.ContractId, ProjectId = contract.ProjectId, PackageId = p.Id, Kind = p.Kind,
                Status = SubscriptionStatuses.AwaitingPayment, PackageName = p.Name,
                // Retail per-panel amount = catalog unit price × panel units (100 % upfront); legacy = the installation-wide price.
                Price = panel ? p.Price * planned.Units : p.Price, DurationMonths = p.DurationMonths, VisitMonths = p.VisitMonths,
                CoveredDisasterTypes = p.CoveredDisasterTypes, CoveragePercent = p.CoveragePercent, PerIncidentLimit = p.PerIncidentLimit, TermLimit = p.TermLimit,
                CoverageScope = panel ? CoverageScopes.Panel : CoverageScopes.Installation, UnitPrice = panel ? p.Price : null, CoveredUnits = panel ? planned.Units : null,
                RenewsSubscriptionId = panel ? planned.Renews : null, CheckoutExpiresAt = expiresAt, CreatedAt = now, UpdatedAt = now,
            };
        }).ToList();
        foreach (var subscription in subscriptions.Where(s => s.CoverageScope == CoverageScopes.Panel))
            foreach (var allocation in panelPlan[subscription.PackageId].Allocations)
                allocation.SubscriptionId = subscription.Id;
        var panelRefused = false;
        var items = subscriptions.Select(s => new CheckoutItem(
            s.Kind == ServicePackageKinds.Maintenance ? PaymentItemTypes.MaintenancePackage : PaymentItemTypes.DisasterPackage, s.Id, null, s.Price)).ToList();

        Func<Guid, CancellationToken, Task<string?>> reserve = async (attemptId, token) =>
        {
            // Per-panel units are re-checked under the contract lock in the checkout transaction: two concurrent purchases cannot both take the
            // same panel units (the second sees the first one's reservation).
            if (panelPlan.Count > 0)
            {
                await data.LockContractAsync(input.ContractId, token);
                foreach (var subscription in subscriptions.Where(s => s.CoverageScope == CoverageScopes.Panel))
                {
                    var from = await PanelTermStartAsync(subscription, now, token);
                    if (await PanelCoverageRules.ConflictAsync(data, subscription, panelPlan[subscription.PackageId].Allocations, from, now, token) is { } conflict)
                    {
                        panelRefused = true;
                        return conflict;
                    }
                }
            }
            else
            {
                // Installation-wide terms: the cross-scope check is repeated under the same contract lock the per-panel path takes, so a concurrent
                // per-panel purchase of the same kind cannot slip between the pre-check and this insert (the database exclusion covers INSTALLATION scope only).
                await data.LockContractAsync(input.ContractId, token);
                foreach (var subscription in subscriptions)
                    if (await PanelCoverageRules.AnyPanelHoldingAsync(data, input.ContractId, subscription.Kind, now, token))
                    {
                        panelRefused = true;
                        return $"Per-panel {subscription.Kind} coverage of this installation is in force or pending; an installation-wide {subscription.Kind} package would overlap it.";
                    }
            }
            // Created in the same transaction as the checkout: no unpaid order exists without its payment, and vice versa.
            foreach (var subscription in subscriptions)
            {
                subscription.CurrentPaymentAttemptId = attemptId;
                data.Add(subscription);
                AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "PURCHASED", customerUserId,
                    subscription.CoverageScope == CoverageScopes.Panel
                        ? $"{subscription.PackageName}: {subscription.UnitPrice:0} VND × {subscription.CoveredUnits} panels = {subscription.Price:0} VND, {subscription.DurationMonths} months"
                          + (subscription.RenewsSubscriptionId is { } renews ? $", renews {renews}" : "")
                        : $"{subscription.PackageName}: {subscription.Price:0} VND, {subscription.DurationMonths} months", now);
                if (subscription.CoverageScope == CoverageScopes.Panel)
                    foreach (var allocation in panelPlan[subscription.PackageId].Allocations) data.Add(allocation);
            }
            return null;
        };
        var checkout = method == PaymentMethods.Cash
            ? await payments.CreateCashCheckoutAsync(customerUserId, input.IncludeContract ? input.ContractId : null, items, reserve, cancellationToken)
            : await payments.CreateCheckoutAsync(customerUserId, input.IncludeContract ? input.ContractId : null, items, expiresAt, reserve, cancellationToken);
        if (!checkout.Succeeded && panelRefused) return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.TermOverlap, checkout.Message!);
        return checkout.Succeeded
            ? OperationResult<PurchaseView>.Ok(new PurchaseView(checkout.Value!, subscriptions.Select(s => s.Id).ToList()))
            : checkout.As<PurchaseView>();
    }

    /// <summary>The earliest instant a per-panel term bought now can start: the renewed term's end, otherwise now.</summary>
    private async Task<DateTimeOffset> PanelTermStartAsync(ServiceSubscription subscription, DateTimeOffset now, CancellationToken token)
        => subscription.RenewsSubscriptionId is { } id && await data.FindSubscriptionAsync(id, false, token) is { EndsAt: { } end } && end > now ? end : now;

    /// <summary>
    /// Validates a per-panel (retail) purchase and plans its allocations: contract fully paid and handed over, real ACTIVE panel rows of this installation,
    /// whole units within each row, no bundled package of the kind still pending, and (renewal) a PANEL-scope term of the same kind that is in force.
    /// The overlap itself is decided again under the contract lock when the checkout is created.
    /// </summary>
    private async Task<OperationResult<Dictionary<Guid, (List<ServiceCoverageAllocation> Allocations, int Units, Guid? Renews)>>> PlanPanelPurchaseAsync(
        ContractFacts contract, IReadOnlyList<ServicePackage> packages, PurchaseInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OperationResult<Dictionary<Guid, (List<ServiceCoverageAllocation>, int, Guid?)>> Fail(string code, string message, object? details = null)
            => OperationResult<Dictionary<Guid, (List<ServiceCoverageAllocation>, int, Guid?)>>.Fail(code, message, details);
        if (input.IncludeContract)
            return Fail(AfterSalesErrorCodes.PricingUnitMismatch, "Per-panel packages are bought after the installation; they cannot be paid together with the contract.");
        if (contract.ProjectId is not { } projectId || contract.HandoverAt is null)
            return Fail(AfterSalesErrorCodes.NotHandedOver, "Per-panel packages are bought for the panels of a handed-over installation.");
        if (input.RenewsSubscriptionId is not null && packages.Count != 1)
            return Fail(AfterSalesErrorCodes.ValidationFailed, "A renewal names exactly one package.");

        var commitments = await data.ListContractServicePackagesAsync(contract.ContractId, false, cancellationToken);
        var panels = PanelCoverageRules.ActivePanels(await data.ListAssetsAsync(projectId, false, cancellationToken)).ToDictionary(a => a.Id);
        var plan = new Dictionary<Guid, (List<ServiceCoverageAllocation>, int, Guid?)>();
        foreach (var package in packages)
        {
            if (commitments.Any(c => c.Kind == package.Kind && c.Status is ContractServicePackageStatuses.Committed or ContractServicePackageStatuses.ReconciliationRequired))
                return Fail(AfterSalesErrorCodes.TermOverlap,
                    $"The {package.Kind} package included in the installation contract is not provisioned yet (reconciliation pending); it is not sold again.");
            var targets = input.TargetAssets?.ToList();
            Guid? renews = null;
            if (input.RenewsSubscriptionId is { } renewedId)
            {
                var renewed = await data.FindSubscriptionAsync(renewedId, false, cancellationToken);
                if (renewed is null || renewed.ContractId != contract.ContractId || renewed.CustomerUserId != contract.CustomerUserId)
                    return Fail(AfterSalesErrorCodes.SubscriptionNotFound, "The term to renew was not found.");
                if (renewed.CoverageScope != CoverageScopes.Panel || renewed.Kind != package.Kind || !SubscriptionStatuses.HoldsTerm(renewed.Status)
                    || renewed.EndsAt is not { } end || end <= now)
                    return Fail(AfterSalesErrorCodes.InvalidState, "Only a per-panel term of the same kind that is in force can be renewed; otherwise buy new coverage.");
                renews = renewed.Id;
                var renewedAllocations = await data.ListCoverageAllocationsAsync([renewed.Id], cancellationToken);
                targets ??= renewedAllocations.Where(a => panels.ContainsKey(a.InstalledAssetId)).Select(a => new AssetUnitsInput(a.InstalledAssetId, a.UnitCount)).ToList();
                foreach (var t in targets)
                    if (renewedAllocations.Where(a => a.InstalledAssetId == t.AssetId).Sum(a => a.UnitCount) < t.Units)
                        return Fail(AfterSalesErrorCodes.PanelSelectionInvalid, "A renewal covers only panels of the renewed term.", new { assetId = t.AssetId });
            }
            if (targets is not { Count: > 0 })
                return Fail(AfterSalesErrorCodes.PricingUnitMismatch,
                    $"{package.Name} is priced per panel: give targetAssets (panel rows and units) from GET /api/customers/me/after-sales/contracts/{{contractId}}/panel-coverage.");
            if (targets.GroupBy(t => t.AssetId).Any(g => g.Count() > 1))
                return Fail(AfterSalesErrorCodes.PanelSelectionInvalid, "Name each panel row once.");
            var allocations = new List<ServiceCoverageAllocation>();
            foreach (var t in targets)
            {
                if (!panels.TryGetValue(t.AssetId, out var asset))
                    return Fail(AfterSalesErrorCodes.PanelSelectionInvalid, "Only active installed panels of this installation can be protected.", new { assetId = t.AssetId });
                if (t.Units < 1 || t.Units > asset.Quantity)
                    return Fail(AfterSalesErrorCodes.PanelSelectionInvalid, $"Units of {PanelCoverageRules.Describe(asset)} must be between 1 and {asset.Quantity}.",
                        new { assetId = t.AssetId });
                allocations.Add(new ServiceCoverageAllocation
                {
                    Id = Guid.NewGuid(), ContractId = contract.ContractId, InstalledAssetId = asset.Id, Kind = package.Kind, UnitCount = t.Units, CreatedAt = now,
                });
            }
            var units = allocations.Sum(a => a.UnitCount);
            if (input.Quantity is { } quantity && quantity != units)
                return Fail(AfterSalesErrorCodes.PanelSelectionInvalid, $"quantity ({quantity}) must equal the units of targetAssets ({units}).");
            plan[package.Id] = (allocations, units, renews);
        }
        return OperationResult<Dictionary<Guid, (List<ServiceCoverageAllocation>, int, Guid?)>>.Ok(plan);
    }

    public async Task<IReadOnlyList<SubscriptionView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<SubscriptionView>();
        foreach (var s in await data.ListSubscriptionsForCustomerAsync(customerUserId, cancellationToken)) list.Add(await views.SubscriptionAsync(s, cancellationToken));
        return list;
    }

    // ================================================================== cancellation before activation

    /// <summary>
    /// Before payment the customer may cancel one purchased package. An unpaid order is cancelled at once; its PayOS checkout is one
    /// link for every line, so the other unpaid packages of that checkout become CHECKOUT_EXPIRED (buy them again) and a contract line is
    /// simply paid again. A paid or active package cannot be cancelled online (cancelling paid money needs a refund decision that this
    /// system does not handle): 409 AFTER_SALES_PAID_CANCELLATION_UNSUPPORTED.
    /// </summary>
    public async Task<OperationResult<SubscriptionView>> RequestCancellationAsync(Guid customerUserId, Guid subscriptionId, string? reason, CancellationToken cancellationToken)
    {
        var why = Text.Trim(reason, 500);
        if (why is null) return OperationResult<SubscriptionView>.Fail(AfterSalesErrorCodes.ReasonRequired, "A reason is required.");
        var subscription = await data.FindSubscriptionAsync(subscriptionId, false, cancellationToken);
        if (subscription is null || subscription.CustomerUserId != customerUserId)
            return OperationResult<SubscriptionView>.Fail(AfterSalesErrorCodes.SubscriptionNotFound, "Package was not found.");
        if (subscription.Status == SubscriptionStatuses.AwaitingPayment && subscription.CurrentPaymentAttemptId is { } attemptId)
        {
            // Stop the payment first; if it was paid meanwhile the package continues as a paid one.
            var attempt = await payments.CancelAttemptAsync(attemptId, "Package cancelled by customer", cancellationToken);
            if (attempt?.Status == PaymentStatuses.Paid) subscription = await data.FindSubscriptionAsync(subscriptionId, false, cancellationToken);
        }
        return await SubscriptionMutateAsync(subscriptionId, async (s, now, token) =>
        {
            switch (s.Status)
            {
                case SubscriptionStatuses.AwaitingPayment or SubscriptionStatuses.CheckoutExpired:
                    s.Status = SubscriptionStatuses.Cancelled;
                    s.CancelledAt = now;
                    s.CancellationReason = why;
                    AfterSalesHistory.Record(data, EventSubjects.Subscription, s.Id, "CANCELLED_UNPAID", customerUserId, why, now);
                    await ReleaseSiblingsOfCancelledCheckoutAsync(s, now, token);
                    return OperationResult<bool>.Ok(true);
                case SubscriptionStatuses.Paid or SubscriptionStatuses.CancellationRequested or SubscriptionStatuses.Active:
                    return OperationResult<bool>.Fail(AfterSalesErrorCodes.PaidCancellationUnsupported,
                        "A paid package cannot be cancelled online: refunds are not handled in this system. Contact SmartSolar.");
                default:
                    return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, $"A {s.Status} package cannot be cancelled online.");
            }
        }, cancellationToken);
    }

    /// <summary>The other unpaid packages that shared the cancelled checkout link can no longer be paid with it.</summary>
    private async Task ReleaseSiblingsOfCancelledCheckoutAsync(ServiceSubscription cancelled, DateTimeOffset now, CancellationToken token)
    {
        if (cancelled.CurrentPaymentAttemptId is not { } attemptId) return;
        foreach (var sibling in (await data.ListSubscriptionsForContractAsync(cancelled.ContractId, true, token))
                     .Where(x => x.Id != cancelled.Id && x.CurrentPaymentAttemptId == attemptId && x.Status == SubscriptionStatuses.AwaitingPayment))
        {
            sibling.Status = SubscriptionStatuses.CheckoutExpired;
            sibling.UpdatedAt = now;
            sibling.Revision++;
            AfterSalesHistory.Record(data, EventSubjects.Subscription, sibling.Id, "CHECKOUT_CANCELLED", cancelled.CustomerUserId,
                $"the shared checkout was cancelled with {cancelled.PackageName}", now);
        }
    }

    /// <summary>
    /// Manager decision on a cancellation request that a customer made before this system stopped handling refunds (status
    /// CANCELLATION_REQUESTED). Rejecting puts the package back to PAID. Approving is refused: it would cancel a paid package and owe the
    /// customer money, which needs a business decision outside this system.
    /// </summary>
    public async Task<OperationResult<SubscriptionView>> DecideCancellationAsync(Guid managerUserId, Guid subscriptionId, bool approve, string? note,
        CancellationToken cancellationToken)
        => await SubscriptionMutateAsync(subscriptionId, (s, now, _) =>
        {
            if (s.Status != SubscriptionStatuses.CancellationRequested) return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "No cancellation is waiting."));
            if (approve)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.PaidCancellationUnsupported,
                    "A paid package cannot be cancelled: refunds are not handled in this system. Reject the request or settle the money outside the system first."));
            s.Status = SubscriptionStatuses.Paid;
            s.CancellationRequestedAt = null;
            AfterSalesHistory.Record(data, EventSubjects.Subscription, s.Id, "CANCELLATION_REJECTED", managerUserId, Text.Trim(note, 500), now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    private async Task<OperationResult<SubscriptionView>> SubscriptionMutateAsync(Guid subscriptionId,
        Func<ServiceSubscription, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var s = await data.FindSubscriptionAsync(subscriptionId, true, token);
                if (s is null) return (OperationResult<bool>.Fail(AfterSalesErrorCodes.SubscriptionNotFound, "Package was not found."), false);
                var now = time.GetUtcNow();
                var outcome = await change(s, now, token);
                if (!outcome.Succeeded) return (outcome, false);
                s.UpdatedAt = now;
                s.Revision++;
                await data.SaveChangesAsync(token);
                return (outcome, true);
            }, cancellationToken);
            return result.Succeeded
                ? OperationResult<SubscriptionView>.Ok(await views.SubscriptionAsync((await data.FindSubscriptionAsync(subscriptionId, false, cancellationToken))!, cancellationToken))
                : result.As<SubscriptionView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<SubscriptionView>.Fail(AfterSalesErrorCodes.StaleRevision, "The package changed meanwhile. Reload and try again.");
        }
    }
}
