using System.Text.Json;
using FluentValidation;
using SmartSolar.Modules.Catalog.Constants;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>Field rules shared by create and update.</summary>
public abstract class ProductDetailsValidator<T> : AbstractValidator<T>
    where T : IProductDetails
{
    private const decimal MaxUnitPriceExclusive = 10_000_000_000_000m;

    protected ProductDetailsValidator()
    {
        RuleFor(x => x.Sku)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.ProductType)
            .NotEmpty()
            .MaximumLength(30);

        RuleFor(x => x.Category)
            .MaximumLength(50);

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(255);

        RuleFor(x => x.Brand)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.Model)
            .MaximumLength(100);

        RuleFor(x => x.Unit)
            .NotEmpty()
            .MaximumLength(20);

        // numeric(15,2): at most 13 integer digits and 2 decimal places.
        RuleFor(x => x.UnitPrice)
            .NotNull()
            .GreaterThanOrEqualTo(0)
            .LessThan(MaxUnitPriceExclusive)
            .Must(price => decimal.Round(price!.Value, 2) == price.Value)
            .When(x => x.UnitPrice.HasValue, ApplyConditionTo.CurrentValidator)
            .WithMessage("'Unit Price' must have at most 2 decimal places.");

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3);

        RuleFor(x => x.WarrantyMonth)
            .GreaterThanOrEqualTo(0)
            .When(x => x.WarrantyMonth.HasValue);

        // Physical values are optional for most types, but never zero or negative.
        RuleFor(x => x.RatedPowerW)
            .GreaterThan(0)
            .When(x => x.RatedPowerW.HasValue);

        RuleFor(x => x.WidthMm)
            .GreaterThan(0)
            .When(x => x.WidthMm.HasValue);

        RuleFor(x => x.HeightMm)
            .GreaterThan(0)
            .When(x => x.HeightMm.HasValue);

        When(x => IsSolarPanel(x.ProductType), () =>
        {
            RuleFor(x => x.RatedPowerW)
                .NotNull()
                .WithMessage("'Rated Power W' is required for SOLAR_PANEL products.");

            RuleFor(x => x.WidthMm)
                .NotNull()
                .WithMessage("'Width Mm' is required for SOLAR_PANEL products.");

            RuleFor(x => x.HeightMm)
                .NotNull()
                .WithMessage("'Height Mm' is required for SOLAR_PANEL products.");
        });

        RuleFor(x => x.Spec)
            .Must(BeJsonObject)
            .When(x => x.Spec is not null)
            .WithMessage("'Spec' must be a JSON object.");
    }

    private static bool IsSolarPanel(string? productType)
        => productType is not null && ProductTypes.Normalize(productType) == ProductTypes.SolarPanel;

    private static bool BeJsonObject(string? spec)
    {
        try
        {
            using var document = JsonDocument.Parse(spec!);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class CreateProductCommandValidator : ProductDetailsValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Status)
            .Must(status => ProductStatusCodes.TryParse(status, out _))
            .When(x => x.Status is not null)
            .WithMessage($"'Status' must be {ProductStatusCodes.Active} or {ProductStatusCodes.Inactive}.");
    }
}

public sealed class UpdateProductCommandValidator : ProductDetailsValidator<UpdateProductCommand>
{
}

public sealed class ChangeProductStatusCommandValidator : AbstractValidator<ChangeProductStatusCommand>
{
    public ChangeProductStatusCommandValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(status => ProductStatusCodes.TryParse(status, out _))
            .WithMessage($"'Status' must be {ProductStatusCodes.Active} or {ProductStatusCodes.Inactive}.");
    }
}
