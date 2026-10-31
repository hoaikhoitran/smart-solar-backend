using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.ListMine;

/// <summary>Paging of the customer's lists; the controller has already validated it (page ≥ 1, 1 ≤ pageSize ≤ 100).</summary>
public sealed record CustomerListPage(int Page, int PageSize)
{
    public const int MaxPageSize = 100;
    public int Skip => (Page - 1) * PageSize;
}

/// <summary>
/// The signed-in customer's own sites and pre-surveys. Only rows of the customer linked to <c>userId</c> are returned
/// (a user without a customer profile simply gets an empty page).
/// </summary>
public sealed class CustomerListingHandler(IPreSurveyUnitOfWork unitOfWork)
{
    public static readonly IReadOnlyList<string> PreSurveyStatuses = ["DRAFT", "SUBMITTED"];
    public static readonly IReadOnlyList<string> PreSurveySortFields = ["updatedAt", "createdAt"];

    public async Task<PagedResult<CustomerSiteListItem>> ListSitesAsync(Guid userId, CustomerListPage page, CancellationToken cancellationToken)
    {
        var (items, total) = await unitOfWork.ListCustomerSitesAsync(userId, page.Skip, page.PageSize, cancellationToken);
        return PagedResult<CustomerSiteListItem>.Create(items, page.Page, page.PageSize, total);
    }

    /// <param name="status">"DRAFT", "SUBMITTED" or null (all); already validated.</param>
    /// <param name="sortBy">"updatedAt" (default) or "createdAt"; newest first unless <paramref name="ascending"/>.</param>
    public async Task<PagedResult<CustomerPreSurveyListItem>> ListPreSurveysAsync(Guid userId, string? status, string? sortBy, bool ascending,
        CustomerListPage page, CancellationToken cancellationToken)
    {
        PreSurveyStatus? filter = status?.ToUpperInvariant() switch
        {
            "DRAFT" => PreSurveyStatus.Draft,
            "SUBMITTED" => PreSurveyStatus.Submitted,
            _ => null,
        };
        var byCreatedAt = string.Equals(sortBy, "createdAt", StringComparison.OrdinalIgnoreCase);
        var (items, total) = await unitOfWork.ListCustomerPreSurveysAsync(userId, filter, byCreatedAt, ascending, page.Skip, page.PageSize, cancellationToken);
        return PagedResult<CustomerPreSurveyListItem>.Create(items, page.Page, page.PageSize, total);
    }
}
