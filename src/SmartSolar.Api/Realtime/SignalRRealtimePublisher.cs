using Microsoft.AspNetCore.SignalR;
using SmartSolar.Modules.Common.Realtime;

namespace SmartSolar.Api.Realtime;

/// <summary>
/// Sends each committed notification to its recipient's connections only (<c>Clients.User</c>; never <c>Clients.All</c>, never a client-chosen group).
/// Awaited by the post-commit hook within its timeout; a failure for one recipient does not stop the others; nothing is thrown.
/// </summary>
public sealed class SignalRRealtimePublisher(IHubContext<RealtimeHub> hub, ILogger<SignalRRealtimePublisher> logger) : IRealtimeNotificationPublisher
{
    public async Task PublishAsync(IReadOnlyList<RealtimeNotification> notifications, CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var notification in notifications)
        {
            if (notification.RecipientUserId == Guid.Empty) continue;
            try
            {
                await hub.Clients.User(notification.RecipientUserId.ToString("D"))
                    .SendAsync(RealtimeHub.ClientMethod, RealtimeEnvelope.From(notification), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                if (failed == 1) logger.LogWarning(ex, "Realtime: sending a {EventType} event failed.", notification.EventType);
            }
        }
        if (failed > 1) logger.LogWarning("Realtime: {Failed} of {Total} events could not be sent.", failed, notifications.Count);
    }
}
