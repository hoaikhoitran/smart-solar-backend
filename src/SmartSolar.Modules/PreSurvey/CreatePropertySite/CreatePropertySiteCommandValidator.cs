using FluentValidation;

namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public sealed class CreatePropertySiteCommandValidator
    : AbstractValidator<CreatePropertySiteCommand>
{
    public CreatePropertySiteCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(150);

        RuleFor(x => x.Province)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.District)
            .MaximumLength(100);

        RuleFor(x => x.Ward)
            .MaximumLength(100);

        RuleFor(x => x.StreetLine)
            .MaximumLength(255);

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90)
            .When(x => x.Latitude.HasValue);

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180)
            .When(x => x.Longitude.HasValue);

        RuleFor(x => x.SurfaceMaterial)
            .MaximumLength(100);

        RuleFor(x => x.Note)
            .MaximumLength(1000);
    }
}