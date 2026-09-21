using MassTransit;
using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Infrastructure.Messaging;

internal sealed class MassTransitIntegrationEventPublisher
    : IIntegrationEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitIntegrationEventPublisher(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task PublishAsync<TEvent>(
        TEvent integrationEvent,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return _publishEndpoint.Publish(integrationEvent, cancellationToken);
    }
}
