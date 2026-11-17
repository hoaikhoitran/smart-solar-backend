using System.Text.RegularExpressions;
using FluentValidation;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>
/// Option B: book an on-site survey directly for one of the customer's property sites. No pre-survey, surface assessment or simulation is
/// needed. Contact: <paramref name="ContactName"/> / <paramref name="ContactPhone"/> when someone else meets the technician; otherwise the
/// account's name and phone (an account without a phone must give <paramref name="ContactPhone"/>).
/// </summary>
public sealed record DirectSurveyBookingCommand(Guid CustomerUserId, Guid PropertySiteId, DateTimeOffset StartsAt, string? ContactName, string? ContactPhone,
    string? Note);

/// <summary>
/// The survey request the booking is attached to and, when a technician capacity could be held, the booking. <see cref="RequestCreated"/> is
/// false when the site's open direct request was reused (a retry, or a second attempt after a failed slot).
/// </summary>
public sealed record DirectSurveyBookingView(Guid SurveyRequestId, string Source, bool RequestCreated, BookingConfirmation Booking);

public sealed partial class DirectSurveyBookingCommandValidator : AbstractValidator<DirectSurveyBookingCommand>
{
    public DirectSurveyBookingCommandValidator()
    {
        RuleFor(x => x.CustomerUserId).NotEmpty();
        RuleFor(x => x.PropertySiteId).NotEmpty().WithMessage("'propertySiteId' is required.");
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("'startsAt' is required (ISO 8601 with offset, e.g. 2026-10-20T08:00:00+07:00).");
        RuleFor(x => x.ContactName).MaximumLength(200);
        RuleFor(x => x.ContactPhone!).Must(DirectSurveyBookingHandler.IsPhone).When(x => !string.IsNullOrWhiteSpace(x.ContactPhone))
            .WithMessage("'contactPhone' must be a phone number of 8–15 digits (an optional leading + and spaces, dots or dashes are allowed).");
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}

/// <summary>
/// Creates (or reuses) the site's DIRECT_BOOKING survey request, then books it with the SAME booking rules as a pre-survey request
/// (<see cref="BookSurveyAppointmentHandler"/>: ownership, booking horizon and slot grid, zone coverage, verified technician capacity, one
/// active appointment, open dispatch request). Nothing is confirmed without capacity: a coverage or capacity failure is returned with
/// <c>details.surveyRequestId</c>, the request stays Pending and the customer can pick another time
/// (<c>POST /api/customers/me/survey-requests/{id}/booking</c>), ask for a dispatch where <c>dispatchAvailable</c>, or withdraw it.
/// A site without an official location (SITE_LOCATION_REQUIRED) gets no request at all. No Sales claim is needed.
/// </summary>
public sealed partial class DirectSurveyBookingHandler(
    ISchedulingUnitOfWork unitOfWork,
    BookSurveyAppointmentHandler booking,
    TimeProvider time)
{
    public const string SurveyRequestAlreadyOpen = "SURVEY_REQUEST_ALREADY_OPEN";
    public const string ContactPhoneRequired = "CONTACT_PHONE_REQUIRED";

    public static bool IsPhone(string value)
    {
        var trimmed = value.Trim();
        if (!PhoneShape().IsMatch(trimmed)) return false;
        var digits = trimmed.Count(char.IsAsciiDigit);
        return digits is >= 8 and <= 15;
    }

    [GeneratedRegex(@"^\+?[0-9][0-9 .\-]{6,22}$")]
    private static partial Regex PhoneShape();

    public async Task<SchedulingResult<DirectSurveyBookingView>> HandleAsync(DirectSurveyBookingCommand command, CancellationToken cancellationToken)
    {
        var site = await unitOfWork.GetPropertySiteAsync(command.PropertySiteId, cancellationToken);
        if (site is null || site.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<DirectSurveyBookingView>.Fail(SchedulingErrorCodes.PropertySiteNotFound, "Property site was not found.");

        // A site the scheduler cannot place (no official commune) can never be booked or dispatched: no request is opened.
        var coverage = await unitOfWork.GetSiteCoverageAsync(site.PropertySiteId, cancellationToken);
        if (coverage.Status == CoverageStatuses.SiteLocationRequired)
            return CapacityFailures.Fail<DirectSurveyBookingView>(CoverageStatuses.SiteLocationRequired, CoverageMessages.Describe(CoverageStatuses.SiteLocationRequired));

        var contactPhone = string.IsNullOrWhiteSpace(command.ContactPhone) ? null : command.ContactPhone.Trim();
        if (contactPhone is null && string.IsNullOrWhiteSpace(await unitOfWork.GetUserPhoneAsync(command.CustomerUserId, cancellationToken)))
            return SchedulingResult<DirectSurveyBookingView>.Fail(ContactPhoneRequired,
                "Your account has no phone number: give 'contactPhone' so the technician can reach you.");

        var now = time.GetUtcNow();
        var opening = await unitOfWork.OpenDirectSurveyRequestAsync(new SurveyRequest
        {
            Id = Guid.NewGuid(),
            PreSurveyId = null,
            PropertySiteId = site.PropertySiteId,
            Source = SurveyRequestSources.DirectBooking,
            ContactName = string.IsNullOrWhiteSpace(command.ContactName) ? null : command.ContactName.Trim(),
            ContactPhone = contactPhone,
            Status = SurveyRequestStatus.Pending,
            SubmittedAt = now,
        }, cancellationToken);
        if (opening.ConflictingSource is not null)
            return SchedulingResult<DirectSurveyBookingView>.Fail(SurveyRequestAlreadyOpen,
                "This site already has an open survey request from a pre-survey: book or withdraw that request instead.",
                new { surveyRequestId = opening.SurveyRequestId, source = opening.ConflictingSource });

        var booked = await booking.HandleAsync(new BookSurveyAppointmentCommand(command.CustomerUserId, opening.SurveyRequestId, command.StartsAt, command.Note),
            cancellationToken);
        if (!booked.Succeeded)
        {
            var coverageDetails = booked.Details as CapacityFailures.CoverageDetails;
            return SchedulingResult<DirectSurveyBookingView>.Fail(booked.ErrorCode!, booked.Message!, new
            {
                surveyRequestId = opening.SurveyRequestId,
                requestCreated = opening.Created,
                coverageStatus = coverageDetails?.CoverageStatus,
                dispatchAvailable = coverageDetails?.DispatchAvailable ?? false,
            });
        }
        return SchedulingResult<DirectSurveyBookingView>.Ok(new DirectSurveyBookingView(opening.SurveyRequestId, SurveyRequestSources.DirectBooking,
            opening.Created, booked.Value!));
    }
}
