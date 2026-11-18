using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Common.Paging;

/// <summary>
/// Opt-in server-side paging for lists that existed before paging (2026-11-18 remediation). When the caller sends neither <c>page</c> nor
/// <c>pageSize</c> the endpoint keeps its historical array response (existing clients unchanged); when either is sent the endpoint answers
/// <see cref="PagedResult{T}"/> (the repository's paging shape: items, page, pageSize, totalItems, totalPages) computed in SQL with a stable order.
/// Same limits as the other paged endpoints: <c>page</c> 1–10 000, <c>pageSize</c> 1–100 (default 20).
/// </summary>
public sealed record PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 10_000;

    public int Skip => (Page - 1) * PageSize;

    /// <summary>Null value = no paging requested (legacy array); a failure = 400 VALIDATION_FAILED with per-field errors.</summary>
    public static OperationResult<PageRequest?> Parse(int? page, int? pageSize)
    {
        if (page is null && pageSize is null) return OperationResult<PageRequest?>.Ok(null);
        var errors = new Dictionary<string, string[]>();
        if (page is < 1 or > MaxPage) errors["page"] = [$"'page' must be between 1 and {MaxPage}."];
        if (pageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"'pageSize' must be between 1 and {MaxPageSize}."];
        return errors.Count > 0
            ? OperationResult<PageRequest?>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors)
            : OperationResult<PageRequest?>.Ok(new PageRequest(page ?? 1, pageSize ?? DefaultPageSize));
    }

    public PagedResult<T> Result<T>(IReadOnlyList<T> items, int total) => PagedResult<T>.Create(items, Page, PageSize, total);
}
