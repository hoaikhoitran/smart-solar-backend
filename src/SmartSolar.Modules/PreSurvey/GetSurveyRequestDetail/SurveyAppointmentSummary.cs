namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

/// <summary>
/// The survey request's active appointment as Sales sees it, including the internally reserved
/// technician (customers never see that). Null when the customer has not booked yet.
/// </summary>
public sealed record SurveyAppointmentSummary(
    Guid AppointmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    Guid ReservedTechnicianUserId,
    string ReservedTechnicianName,
    Guid? AssignedTechnicianUserId,
    string? AssignedTechnicianName,
    string? CustomerNote,
    DateTimeOffset BookedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? AcknowledgedAt);
