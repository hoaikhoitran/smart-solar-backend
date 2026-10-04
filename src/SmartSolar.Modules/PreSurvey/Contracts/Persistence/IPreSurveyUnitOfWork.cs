using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

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
    Task<bool> TrySubmitPreSurveyAsync(
        SurveyRequest surveyRequest,
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
  
}