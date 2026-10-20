using FluentValidation;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed class BookSurveyAppointmentCommandValidator : AbstractValidator<BookSurveyAppointmentCommand>
{
    public BookSurveyAppointmentCommandValidator()
    {
        RuleFor(x => x.CustomerUserId).NotEmpty();
        RuleFor(x => x.SurveyRequestId).NotEmpty();
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("'startsAt' is required (ISO 8601 with offset, e.g. 2026-10-20T08:00:00+07:00).");
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}

public sealed class RescheduleSurveyAppointmentCommandValidator : AbstractValidator<RescheduleSurveyAppointmentCommand>
{
    public RescheduleSurveyAppointmentCommandValidator()
    {
        RuleFor(x => x.CustomerUserId).NotEmpty();
        RuleFor(x => x.SurveyRequestId).NotEmpty();
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("'startsAt' is required.");
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}

public sealed class AssignTechnicianCommandValidator : AbstractValidator<AssignTechnicianCommand>
{
    public AssignTechnicianCommandValidator()
    {
        RuleFor(x => x.SaleUserId).NotEmpty();
        RuleFor(x => x.SurveyRequestId).NotEmpty();
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class OverrideTechnicianCommandValidator : AbstractValidator<OverrideTechnicianCommand>
{
    public OverrideTechnicianCommandValidator()
    {
        RuleFor(x => x.ManagerUserId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SetSalesOwnerCommandValidator : AbstractValidator<SetSalesOwnerCommand>
{
    public SetSalesOwnerCommandValidator()
    {
        RuleFor(x => x.SurveyRequestId).NotEmpty();
        RuleFor(x => x.SaleUserId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
