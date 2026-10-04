using FluentValidation;

namespace SmartSolar.Modules.PreSurvey.CreatePreSurvey;

public sealed class CreatePreSurveyCommandValidator
    : AbstractValidator<CreatePreSurveyCommand>
{
    public CreatePreSurveyCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.PropertySiteId)
            .NotEmpty();

        RuleFor(x => x.TotalAreaM2)
            .GreaterThan(0)
            .When(x => x.TotalAreaM2.HasValue);

        RuleFor(x => x.UsableAreaM2)
            .GreaterThan(0)
            .When(x => x.UsableAreaM2.HasValue);

        RuleFor(x => x.UsableAreaM2)
            .LessThanOrEqualTo(x => x.TotalAreaM2!.Value)
            .When(x =>
                x.UsableAreaM2.HasValue &&
                x.TotalAreaM2.HasValue);

        RuleFor(x => x.TiltDegree)
            .InclusiveBetween(0, 90)
            .When(x => x.TiltDegree.HasValue);

        RuleFor(x => x.AzimuthDegree)
            .InclusiveBetween(0, 360)
            .When(x => x.AzimuthDegree.HasValue);
    }
}