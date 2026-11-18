using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>One contract in the staff directory: who, where, status, and its money figures (30/70: from installments; legacy: PAID = 100%).</summary>
/// <param name="Paid">Applied money only (verified installments, or the whole total of a legacy PAID contract). Pending / refund money is never counted.</param>
public sealed record ContractDirectoryItem(Guid ContractId, string ContractNumber, int Version, string Status, string? PaymentPlan, string Stage,
    Guid CustomerUserId, string CustomerName, string? CustomerPhone, Guid SurveyRequestId, Guid? PropertySiteId, string? PropertyName, Guid QuotationId,
    Guid? ProjectId, string? ProjectStatus, decimal TotalAmount, decimal Paid, decimal Outstanding, bool DepositPaid, bool FullyPaid, bool Overdue,
    DateTimeOffset? NextDueAt, string OwnershipStatus, DateTimeOffset CreatedAt);

/// <summary>
/// Contract directory for Sales, Technician, Manager and Admin (read-all, 2026-11-19): SQL paging, filters (customer, status, plan, request, site,
/// quotation, project, overdue) and search (contract number, customer name / phone / e-mail). Mutations stay scoped by their own endpoints.
/// </summary>
public sealed class ContractDirectoryHandlers(IContractUnitOfWork contracts, TimeProvider time)
{
    public static readonly IReadOnlyList<string> PlanFilters = [.. PaymentPlans.All, "LEGACY"];

    public async Task<OperationResult<PagedResult<ContractDirectoryItem>>> PageAsync(ContractListFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (filter.Status is { } status && !ContractStatuses.All.Contains(status)) errors["status"] = [$"One of {string.Join(", ", ContractStatuses.All)}."];
        if (filter.PaymentPlan is { } plan && !PlanFilters.Contains(plan)) errors["paymentPlan"] = [$"One of {string.Join(", ", PlanFilters)}."];
        if (filter.Search is { Length: > 100 }) errors["search"] = ["At most 100 characters."];
        if (errors.Count > 0) return OperationResult<PagedResult<ContractDirectoryItem>>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);
        var now = time.GetUtcNow();
        var (rows, total) = await contracts.PageContractsAsync(filter with { Now = now }, page.Skip, page.PageSize, cancellationToken);
        var installments = (await contracts.GetInstallmentsForContractsAsync(rows.Select(r => r.ContractId).ToList(), cancellationToken)).ToLookup(i => i.ContractId);
        return OperationResult<PagedResult<ContractDirectoryItem>>.Ok(page.Result(rows.Select(r => Item(r, installments[r.ContractId].ToList(), now)).ToList(), total));
    }

    public static ContractDirectoryItem Item(ContractListRow r, IReadOnlyList<ContractInstallment> installments, DateTimeOffset now)
    {
        if (r.PaymentPlan is null)
        {
            var legacyPaid = r.Status == ContractStatuses.Paid;
            return new ContractDirectoryItem(r.ContractId, r.ContractNumber, r.Version, r.Status, null, r.Status, r.CustomerUserId, r.CustomerName, r.CustomerPhone,
                r.SurveyRequestId, r.PropertySiteId, r.PropertyName, r.QuotationId, r.ProjectId, r.ProjectStatus, r.TotalAmount, legacyPaid ? r.TotalAmount : 0m,
                legacyPaid || !ContractStatuses.IsLive(r.Status) ? 0m : r.TotalAmount, legacyPaid, legacyPaid, false, null, OwnershipStatuses.NotApplicable, r.CreatedAt);
        }
        var s = PaymentPlanCalculator.Summarize(r.TotalAmount, installments, now);
        var probe = new CustomerContract { Status = r.Status, PaymentPlan = r.PaymentPlan, TotalAmount = r.TotalAmount };
        var stage = PaymentPlanViews.Build(probe, installments, [], [], null, now)!.Stage;
        var discarded = r.Status == ContractStatuses.Discarded;
        return new ContractDirectoryItem(r.ContractId, r.ContractNumber, r.Version, r.Status, r.PaymentPlan, stage, r.CustomerUserId, r.CustomerName, r.CustomerPhone,
            r.SurveyRequestId, r.PropertySiteId, r.PropertyName, r.QuotationId, r.ProjectId, r.ProjectStatus, r.TotalAmount, s.Paid, discarded ? 0m : s.Outstanding,
            s.DepositPaid, s.FullyPaid, s.Overdue, s.NextDueAt,
            discarded ? OwnershipStatuses.NotApplicable : s.FullyPaid ? OwnershipStatuses.TransferEligible : OwnershipStatuses.Retained, r.CreatedAt);
    }
}
