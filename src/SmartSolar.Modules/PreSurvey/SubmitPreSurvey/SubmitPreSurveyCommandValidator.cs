using FluentValidation;

namespace SmartSolar.Modules.PreSurvey.SubmitPreSurvey;

public sealed class SubmitPreSurveyCommandValidator
    : AbstractValidator<SubmitPreSurveyCommand>
{
    public SubmitPreSurveyCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.PreSurveyId)
            .NotEmpty();
    }
}