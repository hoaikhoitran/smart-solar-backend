using MimeKit;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

/// <summary>
/// Shared consumer logic of the three one-time-link emails (password reset, email verification, account setup) published from the
/// outbox (<see cref="PasswordResetDispatch"/>):
/// <list type="bullet">
/// <item>no outbox id (message published before the outbox existed): send, and let failures escape to the MassTransit retry;</item>
/// <item>row gone (token replaced by a newer request), SENT (redelivery) or EXPIRED: skip, nothing is sent;</item>
/// <item>SMTP failure: the row is handed back to the outbox (PENDING, URL restored, publish backoff) and the message is acknowledged, so an
/// outage longer than the broker retry window is retried by the worker until the link expires, and the raw URL does not stay in the
/// error queue (audit INT-01). A permanently invalid recipient (<see cref="ParseException"/>) is recorded and not retried;</item>
/// <item>success: the row becomes SENT.</item>
/// </list>
/// Delivery is at-least-once: if the SMTP server accepted the email but recording SENT fails, a retry can send it again.
/// A database failure while reading or updating the row escapes, so the broker retry applies.
/// </summary>
public sealed class AuthLinkEmailDelivery(IPasswordResetDispatchStore dispatches, TimeProvider time, ILogger<AuthLinkEmailDelivery> logger)
{
    public async Task DeliverAsync(Guid? dispatchId, Guid userId, string url, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        if (dispatchId is not { } id)
        {
            await send(cancellationToken);
            return;
        }

        var status = await dispatches.GetStatusAsync(id, cancellationToken);
        if (status is null or PasswordResetDispatchStatuses.Sent or PasswordResetDispatchStatuses.Expired)
        {
            logger.LogInformation("Link email for user {UserId} skipped: dispatch is {Status}.", userId, status ?? "superseded");
            return;
        }

        try
        {
            await send(cancellationToken);
        }
        catch (ParseException ex)
        {
            await dispatches.RecordSendFailureAsync(id, ex.GetType().Name, CancellationToken.None);
            logger.LogWarning("Link email for user {UserId} has an invalid recipient; it is not retried.", userId);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var requeued = await dispatches.ReturnForRetryAsync(id, url, time.GetUtcNow(), ex.GetType().Name, CancellationToken.None);
            // The URL carries the raw token: only the user and the exception type are logged.
            logger.LogWarning("Link email for user {UserId} could not be sent ({Error}); {Outcome}.", userId, ex.GetType().Name,
                requeued ? "returned to the outbox for a later attempt" : "the link is no longer pending, nothing to retry");
            return;
        }

        await dispatches.MarkSentAsync(id, time.GetUtcNow(), CancellationToken.None);
    }
}
