using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Infrastructure.Messaging;

/// <summary>
/// Used when RabbitMQ is disabled so the application runs without a broker.
/// Events are dropped, never queued, so nothing is delivered while it is active.
/// </summary>
internal sealed class NullIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ILogger<NullIntegrationEventPublisher> _logger;

    public NullIntegrationEventPublisher(ILogger<NullIntegrationEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        _logger.LogWarning(
            "Messaging is disabled; dropping integration event {EventType}.",
            typeof(TEvent).Name);

        return Task.CompletedTask;
    }
}
