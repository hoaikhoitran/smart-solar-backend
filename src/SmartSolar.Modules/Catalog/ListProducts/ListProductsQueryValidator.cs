using FluentValidation;

namespace SmartSolar.Modules.Catalog.ListProducts;

public sealed class ListProductsQueryValidator : AbstractValidator<ListProductsQuery>
{
    public ListProductsQueryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1);

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, ListProductsQuery.MaxPageSize);

        RuleFor(x => x.Search)
            .MaximumLength(100);

        RuleFor(x => x.ProductType)
            .MaximumLength(30);

        RuleFor(x => x.Category)
            .MaximumLength(50);

        RuleFor(x => x.Brand)
            .MaximumLength(100);

        RuleFor(x => x.MinPower)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MinPower.HasValue);

        RuleFor(x => x.MaxPower)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MaxPower.HasValue);

        RuleFor(x => x.MinPower)
            .LessThanOrEqualTo(x => x.MaxPower!.Value)
            .When(x => x.MinPower.HasValue && x.MaxPower.HasValue)
            .WithMessage("'Min Power' must not exceed 'Max Power'.");

        RuleFor(x => x.MinPrice)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MinPrice.HasValue);

        RuleFor(x => x.MaxPrice)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MaxPrice.HasValue);

        RuleFor(x => x.MinPrice)
            .LessThanOrEqualTo(x => x.MaxPrice!.Value)
            .When(x => x.MinPrice.HasValue && x.MaxPrice.HasValue)
            .WithMessage("'Min Price' must not exceed 'Max Price'.");

        RuleFor(x => x.SortBy)
            .Must(sortBy => ProductSortOptions.Fields.ContainsKey(sortBy!))
            .When(x => x.SortBy is not null)
            .WithMessage("'Sort By' must be one of: " + string.Join(", ", ProductSortOptions.Fields.Keys) + ".");

        RuleFor(x => x.SortDirection)
            .Must(ProductSortOptions.IsDirection)
            .When(x => x.SortDirection is not null)
            .WithMessage("'Sort Direction' must be 'asc' or 'desc'.");
    }
}
