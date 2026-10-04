using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Api.Contracts.Customers;

public sealed record CreateCustomerProfileRequest(
    CustomerType? CustomerType,
    string? CompanyName,
    string? TaxCode,
    string? Note);