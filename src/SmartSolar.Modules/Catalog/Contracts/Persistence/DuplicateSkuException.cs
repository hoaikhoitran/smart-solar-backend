namespace SmartSolar.Modules.Catalog.Contracts.Persistence;

/// <summary>
/// Raised when a write loses the race against the unique SKU index, so the
/// module can answer with the same duplicate result as the pre-check.
/// </summary>
public sealed class DuplicateSkuException : Exception
{
    public DuplicateSkuException(Exception innerException)
        : base("A product with this SKU already exists.", innerException)
    {
    }
}
