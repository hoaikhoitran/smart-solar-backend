using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

public sealed record PackageInput(string? Kind, string? Name, string? Description, decimal Price, int DurationMonths, IReadOnlyList<int>? VisitMonths,
    IReadOnlyList<string>? CoveredDisasterTypes, decimal? CoveragePercent, decimal? PerIncidentLimit, decimal? TermLimit, bool IsActive);

/// <summary>
/// Packages to buy for one installation contract. When <see cref="IncludeContract"/> is true the SIGNED contract is paid in the same
/// PayOS checkout (its amount stays a separate line; the contract total itself never changes).
/// </summary>
public sealed record PurchaseInput(Guid ContractId, Guid? MaintenancePackageId, Guid? DisasterPackageId, bool IncludeContract);

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
        var previous = (await data.ListSubscriptionsForContractAsync(subscription.ContractId, true, token))
            .Where(s => s.Id != subscription.Id && s.Kind == subscription.Kind && s.EndsAt is not null
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
/// or combined with the contract payment), and cancellation before activation (Manager decision, manual refund).
/// </summary>
public sealed class ServicePackageHandlers(
    IAfterSalesUnitOfWork data,
    PaymentService payments,
    ISchedulingPolicyProvider policies,
    AfterSalesViews views,
    TimeProvider time)
{
    // ================================================================== Admin: catalog

    public async Task<IReadOnlyList<PackageView>> ListPackagesAsync(string? kind, bool activeOnly, CancellationToken cancellationToken)
        => (await data.ListPackagesAsync(kind, activeOnly, cancellationToken)).Select(AfterSalesViews.Package).ToList();

    public async Task<OperationResult<PackageView>> CreatePackageAsync(Guid adminUserId, PackageInput input, CancellationToken cancellationToken)
    {
        if (ValidatePackage(input) is { } error) return OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, error);
        var now = time.GetUtcNow();
        var package = new ServicePackage { Id = Guid.NewGuid(), Kind = input.Kind!, CreatedAt = now };
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
        if (ValidatePackage(input) is { } error) return OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, error);
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<PackageView>>(async token =>
            {
                var package = await data.FindPackageAsync(packageId, true, token);
                if (package is null) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.PackageNotFound, "Package was not found."), false);
                if (package.Revision != expectedRevision) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.StaleRevision, "The package changed meanwhile."), false);
                if (package.Kind != input.Kind) return (OperationResult<PackageView>.Fail(AfterSalesErrorCodes.ValidationFailed, "The package kind cannot change."), false);
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

    private static string? ValidatePackage(PackageInput input)
    {
        if (!ServicePackageKinds.All.Contains(input.Kind ?? "")) return $"Kind must be one of {string.Join(", ", ServicePackageKinds.All)}.";
        if (Text.Trim(input.Name, 150) is null) return "A name is required.";
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
        package.IsActive = input.IsActive;
        package.UpdatedAt = now;
    }

    private static string Describe(ServicePackage p)
        => $"{p.Name}; {p.Price:0} VND; {p.DurationMonths} months; visits {p.VisitMonths}; types {p.CoveredDisasterTypes}; {p.CoveragePercent}% ; "
           + $"per incident {p.PerIncidentLimit:0}; term {p.TermLimit:0}; active {p.IsActive}";

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
        var contract = await data.GetContractAsync(input.ContractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.ProjectNotFound, "Contract was not found.");
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
        var existing = await data.ListSubscriptionsForContractAsync(input.ContractId, false, cancellationToken);
        foreach (var package in packages)
        {
            // No mid-term upgrade and no second queued renewal: one future term per kind may wait for its turn.
            if (existing.Any(s => s.Kind == package.Kind && s.Status is SubscriptionStatuses.Paid or SubscriptionStatuses.CancellationRequested
                                   && (s.StartsAt is null || s.StartsAt > now)))
                return OperationResult<PurchaseView>.Fail(AfterSalesErrorCodes.TermOverlap, "A renewal of this kind is already purchased.");
            if (existing.Any(s => s.Kind == package.Kind && s.Status == SubscriptionStatuses.AwaitingPayment && s.CheckoutExpiresAt > now))
                return OperationResult<PurchaseView>.Fail(PaymentErrorCodes.CheckoutInProgress, "A payment for this kind of package is already in progress.");
        }

        var expiresAt = now.AddMinutes((await policies.GetAsync(cancellationToken)).Get(AfterSalesSettingKeys.CheckoutExpiryMinutes));
        var subscriptions = packages.Select(p => new ServiceSubscription
        {
            Id = Guid.NewGuid(), CustomerUserId = customerUserId, ContractId = input.ContractId, ProjectId = contract.ProjectId, PackageId = p.Id, Kind = p.Kind,
            Status = SubscriptionStatuses.AwaitingPayment, PackageName = p.Name, Price = p.Price, DurationMonths = p.DurationMonths, VisitMonths = p.VisitMonths,
            CoveredDisasterTypes = p.CoveredDisasterTypes, CoveragePercent = p.CoveragePercent, PerIncidentLimit = p.PerIncidentLimit, TermLimit = p.TermLimit,
            CheckoutExpiresAt = expiresAt, CreatedAt = now, UpdatedAt = now,
        }).ToList();
        var items = subscriptions.Select(s => new CheckoutItem(
            s.Kind == ServicePackageKinds.Maintenance ? PaymentItemTypes.MaintenancePackage : PaymentItemTypes.DisasterPackage, s.Id, null, s.Price)).ToList();

        var checkout = await payments.CreateCheckoutAsync(customerUserId, input.IncludeContract ? input.ContractId : null, items, expiresAt, (attemptId, _) =>
        {
            // Created in the same transaction as the checkout: no unpaid order exists without its payment, and vice versa.
            foreach (var subscription in subscriptions)
            {
                subscription.CurrentPaymentAttemptId = attemptId;
                data.Add(subscription);
                AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "PURCHASED", customerUserId,
                    $"{subscription.PackageName}: {subscription.Price:0} VND, {subscription.DurationMonths} months", now);
            }
            return Task.FromResult<string?>(null);
        }, cancellationToken);
        return checkout.Succeeded
            ? OperationResult<PurchaseView>.Ok(new PurchaseView(checkout.Value!, subscriptions.Select(s => s.Id).ToList()))
            : checkout.As<PurchaseView>();
    }

    public async Task<IReadOnlyList<SubscriptionView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<SubscriptionView>();
        foreach (var s in await data.ListSubscriptionsForCustomerAsync(customerUserId, cancellationToken)) list.Add(await views.SubscriptionAsync(s, cancellationToken));
        return list;
    }

    // ================================================================== cancellation before activation

    /// <summary>
    /// Before activation the customer may cancel one purchased package. An unpaid order is cancelled at once; its PayOS checkout is one
    /// link for every line, so the other unpaid packages of that checkout become CHECKOUT_EXPIRED (buy them again) and a contract line is
    /// simply paid again. A paid package becomes a cancellation request for a Manager. Active packages cannot be cancelled online.
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
                case SubscriptionStatuses.Paid:
                    s.Status = SubscriptionStatuses.CancellationRequested;
                    s.CancellationRequestedAt = now;
                    s.CancellationReason = why;
                    AfterSalesHistory.Record(data, EventSubjects.Subscription, s.Id, "CANCELLATION_REQUESTED", customerUserId, why, now);
                    AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, CancellationToken.None), "PACKAGE_CANCELLATION_REQUESTED",
                        $"A customer asks to cancel {s.PackageName} before activation.", now, subscriptionId: s.Id);
                    return OperationResult<bool>.Ok(true);
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

    /// <summary>Manager decision on a pre-activation cancellation; approval records an APPROVED manual refund (default: the full price).</summary>
    public async Task<OperationResult<SubscriptionView>> DecideCancellationAsync(Guid managerUserId, Guid subscriptionId, bool approve, decimal? refundAmount, string? note,
        CancellationToken cancellationToken)
        => await SubscriptionMutateAsync(subscriptionId, async (s, now, token) =>
        {
            if (s.Status != SubscriptionStatuses.CancellationRequested) return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "No cancellation is waiting.");
            if (!approve)
            {
                s.Status = SubscriptionStatuses.Paid;
                s.CancellationRequestedAt = null;
                AfterSalesHistory.Record(data, EventSubjects.Subscription, s.Id, "CANCELLATION_REJECTED", managerUserId, Text.Trim(note, 500), now);
                return OperationResult<bool>.Ok(true);
            }
            var amount = refundAmount ?? s.Price;
            if (amount < 0 || amount > s.Price || decimal.Truncate(amount) != amount)
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.ValidationFailed, "The refund is a whole VND amount up to the price paid.");
            await CancelPaidAsync(s, managerUserId, amount, $"Package cancelled before activation: {s.CancellationReason}", now, token);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Cancels a paid, not yet active term (customer request or installation cancelled before handover) with an approved refund.</summary>
    public async Task CancelPaidAsync(ServiceSubscription s, Guid? managerUserId, decimal refundAmount, string reason, DateTimeOffset now, CancellationToken token)
    {
        s.Status = SubscriptionStatuses.Cancelled;
        s.CancelledAt = now;
        foreach (var entitlement in await data.ListEntitlementsAsync(s.Id, true, token))
        {
            entitlement.Status = EntitlementStatuses.Expired;
            entitlement.UpdatedAt = now;
            entitlement.Revision++;
        }
        var refund = new RefundRecord
        {
            Id = Guid.NewGuid(), CustomerUserId = s.CustomerUserId, PaymentAttemptId = s.CurrentPaymentAttemptId, SubscriptionId = s.Id, OriginalAmount = s.Price,
            ApprovedAmount = managerUserId is null ? null : refundAmount, Reason = Text.Trim(reason, 1000)!,
            Status = managerUserId is null ? RefundStatuses.PendingApproval : RefundStatuses.Approved, ApprovedByUserId = managerUserId,
            ApprovedAt = managerUserId is null ? null : now, CreatedAt = now, UpdatedAt = now,
        };
        data.Add(refund);
        AfterSalesHistory.Record(data, EventSubjects.Subscription, s.Id, "CANCELLED", managerUserId, reason, now);
        AfterSalesHistory.Record(data, EventSubjects.Refund, refund.Id, managerUserId is null ? "REFUND_REQUESTED" : "REFUND_APPROVED", managerUserId,
            $"{refund.ApprovedAmount ?? refund.OriginalAmount:0} VND for {s.PackageName}", now);
    }

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
