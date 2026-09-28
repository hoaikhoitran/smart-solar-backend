using SmartSolar.Modules.PreSurvey.Entities;

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
}