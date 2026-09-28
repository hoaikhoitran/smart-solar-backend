using SmartSolar.Modules.Catalog.Models;

namespace SmartSolar.Modules.Catalog.ManageProducts;

public enum ProductCommandOutcome
{
    Succeeded = 1,
    NotFound = 2,
    DuplicateSku = 3
}

/// <summary><see cref="Product"/> is set only when the outcome is Succeeded.</summary>
public sealed record ProductCommandResult(ProductCommandOutcome Outcome, ProductDto? Product)
{
    public static ProductCommandResult Succeeded(ProductDto? product)
        => new(ProductCommandOutcome.Succeeded, product);

    public static ProductCommandResult NotFound() => new(ProductCommandOutcome.NotFound, null);

    public static ProductCommandResult DuplicateSku() => new(ProductCommandOutcome.DuplicateSku, null);
}
