using SmartSolar.Modules.Scheduling.Constants;

namespace SmartSolar.Modules.PreSurvey.GetMySurveyRequests;

/// <summary>
/// The request's active (not cancelled) survey appointment in a list row. Technician fields are filled only for the
/// request's Sales owner (null in the unclaimed queue). <see cref="IsOverdue"/>: the end passed but the survey was not completed.
/// </summary>
public sealed record SurveyRequestAppointmentItem(
    Guid AppointmentId,
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset? AcceptanceDeadlineAt,
    Guid? TechnicianUserId,
    string? TechnicianName,
    bool IsOverdue)
{
    /// <summary>The same summary with <see cref="IsOverdue"/> computed for <paramref name="nowUtc"/>.</summary>
    public SurveyRequestAppointmentItem At(DateTimeOffset nowUtc) => this with { IsOverdue = AppointmentStatuses.IsOverdue(Status, EndsAt, nowUtc) };
}
