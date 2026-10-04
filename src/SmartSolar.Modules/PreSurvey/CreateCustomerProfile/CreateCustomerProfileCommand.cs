using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.CreateCustomerProfile;

public sealed record CreateCustomerProfileCommand(
    Guid UserId,
    CustomerType? CustomerType,
    string? CompanyName,
    string? TaxCode,
    string? Note);