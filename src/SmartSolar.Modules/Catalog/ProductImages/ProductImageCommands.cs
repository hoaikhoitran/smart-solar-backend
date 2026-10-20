namespace SmartSolar.Modules.Catalog.ProductImages;

/// <summary>One image supplied with a new product; the first one becomes primary.</summary>
public sealed record ProductImageInput(string Url, string? AltText);

/// <summary>Appends an image; the first image of a product is always primary.</summary>
public sealed record AddProductImageCommand(Guid ProductId, string Url, string? AltText, bool MakePrimary);

/// <summary><paramref name="ImageIds"/> must be exactly the product's current image set, in the new order.</summary>
public sealed record ReorderProductImagesCommand(Guid ProductId, IReadOnlyList<Guid> ImageIds);

public enum ProductImageOutcome
{
    Succeeded = 1,
    ProductNotFound = 2,
    ImageNotFound = 3,
    Duplicate = 4,
    Conflict = 5,
    LimitReached = 6,
    OrderMismatch = 7
}

/// <summary><see cref="Product"/> (the product with its updated gallery) is set only when the outcome is Succeeded.</summary>
public sealed record ProductImageResult(ProductImageOutcome Outcome, Models.ProductDto? Product)
{
    public static ProductImageResult Failed(ProductImageOutcome outcome) => new(outcome, null);
}
