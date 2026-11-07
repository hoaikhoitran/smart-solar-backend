using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.PreSurvey.ListMine;

namespace SmartSolar.Modules.PreSurvey.Contracts.Persistence;

public interface IPreSurveyUnitOfWork
{
    Task<Customer?> FindCustomerByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<PropertySite?> FindPropertyByIdAsync(
        Guid propertyId,
        CancellationToken cancellationToken);

    void AddCustomer(Customer customer);

    void AddPropertySite(PropertySite propertySite);

    void AddPreSurvey(Entities.PreSurvey preSurvey);

    Task<int> SaveChangesAsync(
        CancellationToken cancellationToken);
    Task<Entities.PreSurvey?> FindPreSurveyByIdAsync(
        Guid preSurveyId,
        CancellationToken cancellationToken);
    /// <summary>
    /// Atomically moves the request's pre-survey from Draft to Submitted and stores the
    /// request. Returns false, storing nothing, when the pre-survey is no longer a Draft.
    /// </summary>
    /// <remarks>
    /// When <paramref name="expectedRevision"/> is given, the move also requires that no other
    /// write happened since the handler read the pre-survey. The revision is incremented.
    /// </remarks>
    Task<bool> TrySubmitPreSurveyAsync(
        SurveyRequest surveyRequest,
        CancellationToken cancellationToken,
        int? expectedRevision = null);

    /// <summary>
    /// Saves tracked changes to a draft. Returns false, storing nothing, when the pre-survey's
    /// revision changed since it was read (another update, a submission, or a surface edit).
    /// </summary>
    Task<bool> TrySaveDraftChangesAsync(
        CancellationToken cancellationToken);

    /// <summary>Fresh, untracked status read used to explain a failed conditional write.</summary>
    Task<Enums.PreSurveyStatus?> GetPreSurveyStatusAsync(
        Guid preSurveyId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingSurveyRequestItem>>
    GetPendingSurveyRequestsAsync(
        CancellationToken cancellationToken);
    Task<bool> TryClaimSurveyRequestAsync(
        Guid surveyRequestId,
        Guid saleUserId,
        DateTimeOffset assignedAt,
        CancellationToken cancellationToken);
        Task<IReadOnlyList<MySurveyRequestItem>>
    GetSurveyRequestsBySaleAsync(
        Guid saleUserId,
        CancellationToken cancellationToken);
    Task<SurveyRequestDetail?> GetSurveyRequestDetailAsync(
    Guid surveyRequestId,
    CancellationToken cancellationToken);

    /// <summary>One page of the sites of the customer linked to <paramref name="userId"/> (by name), and the total count.</summary>
    Task<(IReadOnlyList<CustomerSiteListItem> Items, int Total)> ListCustomerSitesAsync(
        Guid userId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>One page of that customer's pre-surveys (optionally one status), by UpdatedAt or CreatedAt, and the total count.</summary>
    Task<(IReadOnlyList<CustomerPreSurveyListItem> Items, int Total)> ListCustomerPreSurveysAsync(
        Guid userId, Enums.PreSurveyStatus? status, bool sortByCreatedAt, bool ascending, int skip, int take, CancellationToken cancellationToken);
}