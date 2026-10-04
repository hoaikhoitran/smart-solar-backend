using FluentValidation;

namespace SmartSolar.Modules.PreSurvey.CreateCustomerProfile;

public sealed class CreateCustomerProfileCommandValidator
    : AbstractValidator<CreateCustomerProfileCommand>
{
    public CreateCustomerProfileCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.CompanyName)
            .MaximumLength(255);

        RuleFor(x => x.TaxCode)
            .MaximumLength(50);

        RuleFor(x => x.Note)
            .MaximumLength(1000);
    }
}