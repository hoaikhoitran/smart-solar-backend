using SmartSolar.Modules.Common.Messaging;

namespace SmartSolar.Tests.TestSupport;

public sealed class FakeIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly List<object> _published = new();

    public IReadOnlyList<object> Published => _published;

    /// <summary>When set, publishing throws to simulate a broker outage after commit.</summary>
    public Exception? ThrowOnPublish { get; set; }

    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        if (ThrowOnPublish is not null)
        {
            return Task.FromException(ThrowOnPublish);
        }

        _published.Add(integrationEvent);
        return Task.CompletedTask;
    }

    public IEnumerable<TEvent> PublishedOf<TEvent>() => _published.OfType<TEvent>();
}
