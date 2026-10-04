using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

public sealed class Customer
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public CustomerType? CustomerType { get; set; }
    public string? CompanyName { get; set; }
    public string? TaxCode { get; set; }
    public string? Note { get; set; }

    public UserAccount User { get; set; } = null!;
    public ICollection<PropertySite> PropertySites { get; set; } = [];
}