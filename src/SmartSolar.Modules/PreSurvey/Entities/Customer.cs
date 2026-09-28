using System;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class Customer
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Code { get; set; } = null!;
    public string? CustomerType { get; set; } 
    public string? CompanyName { get; set; }

    public string? TaxCode { get; set; }

    public Guid? AssignedSaleId { get; set; }

    public string Status { get; set; } = null!;

    public string? Note { get; set; }

    public UserAccount User { get; set; } = null!;

    public UserAccount? AssignedSale { get; set; }
    public ICollection<PropertySite> PropertySites { get; set; } = [];

}
