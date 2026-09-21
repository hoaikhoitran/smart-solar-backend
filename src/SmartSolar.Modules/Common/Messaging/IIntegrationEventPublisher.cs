namespace SmartSolar.Modules.Common.Messaging;

/// <summary>
/// Publishes integration events to other modules or services.
/// Publishing is not atomic with database writes.
/// </summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync<TEvent>(
        TEvent integrationEvent,
        CancellationToken cancellationToken = default)
        where TEvent : class;
}
