using SmartSolar.Modules.Common.Time;
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

    /// <summary>
    /// In-app notification to the customer who booked the appointment (what the customer notification endpoint lists). Staff keep their
    /// <see cref="SchedulingNotification"/> rows. Callers skip it when the customer is the actor of the change.
    /// </summary>
    public static void NotifyCustomer(ISchedulingUnitOfWork unitOfWork, TechnicianAppointment appointment, string type, string message, DateTimeOffset nowUtc)
        => unitOfWork.AddNotification(appointment.BookedByUserId, type, message, nowUtc);

    public static string When(DateTimeOffset startsAt, TimeZoneInfo zone) => DisplayTime.Format(startsAt, zone);
}
