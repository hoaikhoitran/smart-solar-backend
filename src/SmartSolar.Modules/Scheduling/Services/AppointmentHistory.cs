using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// Writes appointment history and the notifications it owes, inside the caller's transaction.
/// No delivery channel exists yet, so notifications are recorded as NOT_DELIVERED (never claimed as sent).
/// </summary>
public static class AppointmentHistory
{
    public static TechnicianAppointmentEvent Record(
        ISchedulingUnitOfWork unitOfWork,
        TechnicianAppointment appointment,
        string eventType,
        Guid? actorUserId,
        DateTimeOffset nowUtc,
        Guid? previousTechnicianUserId = null,
        Guid? newTechnicianUserId = null,
        DateTimeOffset? previousStartsAt = null,
        DateTimeOffset? newStartsAt = null,
        string? reason = null,
        params Guid?[] notify)
    {
        var entry = new TechnicianAppointmentEvent
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            SurveyRequestId = appointment.SurveyRequestId,
            EventType = eventType,
            ActorUserId = actorUserId,
            PreviousTechnicianUserId = previousTechnicianUserId,
            NewTechnicianUserId = newTechnicianUserId,
            PreviousStartsAt = previousStartsAt,
            NewStartsAt = newStartsAt,
            Reason = reason is { Length: > 500 } ? reason[..500] : reason,
            CreatedAt = nowUtc,
        };
        unitOfWork.AddAppointmentEvent(entry);

        foreach (var recipient in notify.Where(r => r is not null).Select(r => r!.Value).Distinct())
        {
            unitOfWork.AddNotification(new SchedulingNotification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = recipient,
                AppointmentId = appointment.Id,
                EventId = entry.Id,
                Type = eventType,
                DeliveryStatus = SchedulingNotificationStatuses.NotDelivered,
                CreatedAt = nowUtc,
            });
        }

        return entry;
    }
}
