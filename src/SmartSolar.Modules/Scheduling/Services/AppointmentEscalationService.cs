using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// The background sweep of open survey appointments, run by the escalation worker:
/// <list type="bullet">
/// <item><see cref="CancelNotAcceptedBeforeStartAsync"/>: an open (PENDING_ACCEPTANCE/ESCALATED) appointment whose start passed
/// can never be accepted, assigned, rescheduled or cancelled any more and would block a new booking of its request (one active
/// appointment per request). It is cancelled with reason NOT_ACCEPTED_BEFORE_START (conditional on status, start and revision,
/// so concurrent sweeps cancel it once), a CANCELLED event and a notification for the Sales owner; the capacity hold is released
/// (cancelled rows never count) and the survey request returns to Pending (no scheduled time) so the customer can book again.</item>
/// <item><see cref="EscalateDueAsync"/>: PENDING_ACCEPTANCE appointments whose acceptance deadline passed (and that have not
/// started) move to ESCALATED with a conditional update (a concurrent acceptance wins cleanly), an ESCALATED event and a
/// notification for the Sales owner (unowned requests appear in the escalated queue).</item>
/// <item><see cref="SendAcceptanceRemindersAsync"/>: when <c>scheduling.acceptanceReminderLeadMinutes</c> (current settings, read on every
/// run) is &gt; 0, each PENDING_ACCEPTANCE appointment gets ONE reminder (ACCEPTANCE_REMINDER event + notification for its capacity
/// holder) once its PERSISTED deadline is within that lead. Deduplicated by a conditional update of acceptance_reminder_sent_at; the
/// deadline itself is never recomputed. 0 disables reminders.</item>
/// </list>
/// Queries already treat a pending appointment past its deadline as escalated, and a started open one as cancelled, so correctness
/// never depends on this sweep running. One failing appointment is logged (id only) and skipped; the next run retries it.
/// </summary>
public sealed class AppointmentEscalationService(ISchedulingUnitOfWork unitOfWork, TimeProvider time, SystemSettings.ISchedulingPolicyProvider policies,
    ILogger<AppointmentEscalationService>? logger = null)
{
    public async Task<int> SendAcceptanceRemindersAsync(int limit, CancellationToken cancellationToken)
    {
        var lead = (await policies.GetAsync(cancellationToken)).AcceptanceReminderLeadMinutes;
        if (lead <= 0) return 0;   // disabled
        var sent = 0;
        foreach (var id in await unitOfWork.ListDueAcceptanceRemindersAsync(time.GetUtcNow(), lead, limit, cancellationToken))
        {
            try
            {
                var done = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var now = time.GetUtcNow();
                    if (!await unitOfWork.TryMarkAcceptanceReminderAsync(id, now, token)) return (false, false);
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(id, token);
                    if (appointment is null) return (false, false);
                    AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.AcceptanceReminder, null, now,
                        newTechnicianUserId: appointment.ReservedTechnicianUserId, reason: $"ACCEPTANCE_DUE {appointment.AcceptanceDeadlineAt:O}",
                        notify: [appointment.ReservedTechnicianUserId]);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                if (done) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("Acceptance reminder for survey appointment {AppointmentId} failed ({ErrorType}); retrying on the next run.", id, ex.GetType().Name);
            }
        }
        return sent;
    }

    public async Task<int> CancelNotAcceptedBeforeStartAsync(int limit, CancellationToken cancellationToken)
    {
        var cancelled = 0;
        foreach (var id in await unitOfWork.ListStartedOpenAppointmentsAsync(time.GetUtcNow(), limit, cancellationToken))
        {
            try
            {
                if (await TryCancelNotAcceptedAsync(unitOfWork, time, id, cancellationToken)) cancelled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ids only: exception messages may carry row data.
                logger?.LogWarning("Auto-cancelling survey appointment {AppointmentId} failed ({ErrorType}); retrying on the next run.", id, ex.GetType().Name);
            }
        }
        return cancelled;
    }

    public async Task<int> EscalateDueAsync(int limit, CancellationToken cancellationToken)
    {
        var escalated = 0;
        foreach (var id in await unitOfWork.ListDueEscalationsAsync(time.GetUtcNow(), limit, cancellationToken))
        {
            try
            {
                var done = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var now = time.GetUtcNow();
                    if (!await unitOfWork.TryEscalateAsync(id, now, token)) return (false, false);
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(id, token);
                    if (appointment is null) return (false, false);
                    var owner = appointment.SurveyRequestId is { } rid ? (await unitOfWork.GetSurveyRequestContextAsync(rid, token))?.AssignedSaleId : null;
                    AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Escalated, null, now,
                        newTechnicianUserId: appointment.ReservedTechnicianUserId, reason: "ACCEPTANCE_DEADLINE_PASSED", notify: [owner]);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                if (done) escalated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("Escalating survey appointment {AppointmentId} failed ({ErrorType}); retrying on the next run.", id, ex.GetType().Name);
            }
        }
        return escalated;
    }

    /// <summary>
    /// Cancels one open appointment whose start passed (NOT_ACCEPTED_BEFORE_START) in its own transaction; false when it is not
    /// (or no longer) such an appointment. Shared by the sweep and by a new booking of the same request.
    /// </summary>
    internal static Task<bool> TryCancelNotAcceptedAsync(ISchedulingUnitOfWork unitOfWork, TimeProvider time, Guid appointmentId, CancellationToken cancellationToken)
        => unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var now = time.GetUtcNow();
            var snapshot = await unitOfWork.GetAppointmentAsync(appointmentId, token);
            if (snapshot is null || !AppointmentStatuses.IsOpen(snapshot.Status) || snapshot.StartsAt > now) return (false, false);
            if (!await unitOfWork.TryCancelNotAcceptedAsync(appointmentId, snapshot.Revision, now, token)) return (false, false);
            var appointment = await unitOfWork.FindAppointmentForUpdateAsync(appointmentId, token);
            if (appointment is null) return (false, false);

            Guid? owner = null;
            if (appointment.SurveyRequestId is { } requestId)
            {
                await unitOfWork.SetSurveyRequestScheduleAsync(requestId, null, token);
                owner = (await unitOfWork.GetSurveyRequestContextAsync(requestId, token))?.AssignedSaleId;
            }
            AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Cancelled, null, now,
                previousTechnicianUserId: appointment.ReservedTechnicianUserId, previousStartsAt: appointment.StartsAt,
                reason: CancellationReasons.NotAcceptedBeforeStart, notify: [owner]);
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
}
