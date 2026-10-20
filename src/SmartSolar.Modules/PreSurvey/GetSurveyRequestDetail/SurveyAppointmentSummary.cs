namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

/// <summary>
/// The survey request's active appointment as Sales sees it. <see cref="CapacityHolderUserId"/> is the internal
/// capacity holder (never shown to customers and not an acceptance); <see cref="AssignedTechnicianUserId"/> is
/// the confirmed technician (self-accepted or staff-assigned). Null when the customer has not booked yet.
/// </summary>
public sealed record SurveyAppointmentSummary(
    Guid AppointmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    Guid? CapacityHolderUserId,
    string? CapacityHolderName,
    Guid? AssignedTechnicianUserId,
    string? AssignedTechnicianName,
    string? AssignmentSource,
    string? AssignmentReason,
    string? CustomerNote,
    DateTimeOffset BookedAt,
    DateTimeOffset? AcceptanceDeadlineAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? CompletedAt,
    Guid? OperationalZoneId,
    int Revision);
