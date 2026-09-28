using SmartSolar.Modules.Catalog.Enums;

namespace SmartSolar.Modules.Catalog.Constants;

/// <summary>
/// The external codes for <see cref="ProductStatus"/>, shared by the API
/// contract and the database column so both always agree.
/// </summary>
public static class ProductStatusCodes
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";

    public static string ToCode(ProductStatus status) => status switch
    {
        ProductStatus.Active => Active,
        ProductStatus.Inactive => Inactive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unsupported product status.")
    };

    public static bool TryParse(string? code, out ProductStatus status)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case Active:
                status = ProductStatus.Active;
                return true;
            case Inactive:
                status = ProductStatus.Inactive;
                return true;
            default:
                status = default;
                return false;
        }
    }
}
