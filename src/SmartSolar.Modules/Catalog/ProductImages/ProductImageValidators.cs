using FluentValidation;

namespace SmartSolar.Modules.Catalog.ProductImages;

public sealed class AddProductImageCommandValidator : AbstractValidator<AddProductImageCommand>
{
    public AddProductImageCommandValidator()
        : this(new CatalogOptions())
    {
    }

    public AddProductImageCommandValidator(CatalogOptions options)
    {
        RuleFor(x => x.Url)
            .Custom((url, context) =>
            {
                if (ProductImageRules.UrlError(url, options) is { } error)
                {
                    context.AddFailure(nameof(AddProductImageCommand.Url), error);
                }
            });

        RuleFor(x => x.AltText)
            .Custom((altText, context) =>
            {
                if (ProductImageRules.AltTextError(altText) is { } error)
                {
                    context.AddFailure(nameof(AddProductImageCommand.AltText), error);
                }
            });
    }
}

public sealed class ReorderProductImagesCommandValidator : AbstractValidator<ReorderProductImagesCommand>
{
    public ReorderProductImagesCommandValidator()
    {
        RuleFor(x => x.ImageIds)
            .NotNull();
    }
}
