using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Delegates to a real unit of work but runs <see cref="AfterPreSurveyRead"/> once, right
/// after the handler reads the pre-survey. Tests use it to commit a competing write on
/// another connection exactly between a handler's read and its conditional write.
/// </summary>
public sealed class InterleavingPreSurveyUnitOfWork(IPreSurveyUnitOfWork inner) : IPreSurveyUnitOfWork
{
    public Func<Task>? AfterPreSurveyRead { get; set; }

    public async Task<Modules.PreSurvey.Entities.PreSurvey?> FindPreSurveyByIdAsync(Guid preSurveyId, CancellationToken cancellationToken)
    {
        var preSurvey = await inner.FindPreSurveyByIdAsync(preSurveyId, cancellationToken);
        if (AfterPreSurveyRead is { } hook)
        {
            AfterPreSurveyRead = null;
            await hook();
        }
        return preSurvey;
    }

    public Task<Customer?> FindCustomerByUserIdAsync(Guid userId, CancellationToken cancellationToken) => inner.FindCustomerByUserIdAsync(userId, cancellationToken);
    public Task<PropertySite?> FindPropertyByIdAsync(Guid propertyId, CancellationToken cancellationToken) => inner.FindPropertyByIdAsync(propertyId, cancellationToken);
    public void AddCustomer(Customer customer) => inner.AddCustomer(customer);
    public void AddPropertySite(PropertySite propertySite) => inner.AddPropertySite(propertySite);
    public void AddPreSurvey(Modules.PreSurvey.Entities.PreSurvey preSurvey) => inner.AddPreSurvey(preSurvey);
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);
    public Task<bool> TrySubmitPreSurveyAsync(SurveyRequest surveyRequest, CancellationToken cancellationToken, int? expectedRevision = null) => inner.TrySubmitPreSurveyAsync(surveyRequest, cancellationToken, expectedRevision);
    public Task<bool> TrySaveDraftChangesAsync(CancellationToken cancellationToken) => inner.TrySaveDraftChangesAsync(cancellationToken);
    public Task<PreSurveyStatus?> GetPreSurveyStatusAsync(Guid preSurveyId, CancellationToken cancellationToken) => inner.GetPreSurveyStatusAsync(preSurveyId, cancellationToken);
    public Task<IReadOnlyList<PendingSurveyRequestItem>> GetPendingSurveyRequestsAsync(CancellationToken cancellationToken) => inner.GetPendingSurveyRequestsAsync(cancellationToken);
    public Task<bool> TryClaimSurveyRequestAsync(Guid surveyRequestId, Guid saleUserId, DateTimeOffset assignedAt, CancellationToken cancellationToken) => inner.TryClaimSurveyRequestAsync(surveyRequestId, saleUserId, assignedAt, cancellationToken);
    public Task<IReadOnlyList<MySurveyRequestItem>> GetSurveyRequestsBySaleAsync(Guid saleUserId, CancellationToken cancellationToken) => inner.GetSurveyRequestsBySaleAsync(saleUserId, cancellationToken);
    public Task<SurveyRequestDetail?> GetSurveyRequestDetailAsync(Guid surveyRequestId, CancellationToken cancellationToken) => inner.GetSurveyRequestDetailAsync(surveyRequestId, cancellationToken);
}
