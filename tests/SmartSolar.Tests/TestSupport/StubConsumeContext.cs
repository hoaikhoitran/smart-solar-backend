using System.Diagnostics.CodeAnalysis;
using MassTransit;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Minimal ConsumeContext so a consumer can be exercised without a bus.
/// Only Message and CancellationToken are real; everything else throws if
/// touched. On MassTransit 8.x an in-memory test harness is also possible,
/// which would cover the endpoint wiring this stub cannot.
/// </summary>
public sealed class StubConsumeContext<TMessage> : ConsumeContext<TMessage>
    where TMessage : class
{
    private static NotSupportedException Unsupported([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"StubConsumeContext does not support '{member}'.");

    public StubConsumeContext(TMessage message, CancellationToken cancellationToken)
    {
        Message = message;
        CancellationToken = cancellationToken;
    }

    public TMessage Message { get; }

    public CancellationToken CancellationToken { get; }

    // PipeContext
    public bool HasPayloadType(Type payloadType) => throw Unsupported();

    public bool TryGetPayload<T>([NotNullWhen(true)] out T? payload) where T : class
    {
        payload = null;
        return false;
    }

    public T GetOrAddPayload<T>(PayloadFactory<T> payloadFactory) where T : class => throw Unsupported();

    public T AddOrUpdatePayload<T>(PayloadFactory<T> addFactory, UpdatePayloadFactory<T> updateFactory)
        where T : class => throw Unsupported();

    // MessageContext
    public Guid? MessageId => throw Unsupported();
    public Guid? RequestId => throw Unsupported();
    public Guid? CorrelationId => throw Unsupported();
    public Guid? ConversationId => throw Unsupported();
    public Guid? InitiatorId => throw Unsupported();
    public DateTime? ExpirationTime => throw Unsupported();
    public Uri SourceAddress => throw Unsupported();
    public Uri DestinationAddress => throw Unsupported();
    public Uri ResponseAddress => throw Unsupported();
    public Uri FaultAddress => throw Unsupported();
    public DateTime? SentTime => throw Unsupported();
    public Headers Headers => throw Unsupported();
    public HostInfo Host => throw Unsupported();

    // ConsumeContext
    public ReceiveContext ReceiveContext => throw Unsupported();
    public SerializerContext SerializerContext => throw Unsupported();
    public Task ConsumeCompleted => Task.CompletedTask;
    public IEnumerable<string> SupportedMessageTypes => throw Unsupported();

    public bool HasMessageType(Type messageType) => throw Unsupported();

    public bool TryGetMessage<T>([NotNullWhen(true)] out ConsumeContext<T>? consumeContext) where T : class
    {
        consumeContext = null;
        return false;
    }

    public void AddConsumeTask(Task task) => throw Unsupported();

    public Task NotifyConsumed(TimeSpan duration, string consumerType) => throw Unsupported();

    public Task NotifyFaulted(TimeSpan duration, string consumerType, Exception exception)
        => throw Unsupported();

    public Task NotifyConsumed<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType)
        where T : class => throw Unsupported();

    public Task NotifyFaulted<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception)
        where T : class => throw Unsupported();

    public void Respond<T>(T message) where T : class => throw Unsupported();

    public Task RespondAsync<T>(T message) where T : class => throw Unsupported();

    public Task RespondAsync<T>(T message, IPipe<SendContext<T>> sendPipe) where T : class => throw Unsupported();

    public Task RespondAsync<T>(T message, IPipe<SendContext> sendPipe) where T : class => throw Unsupported();

    public Task RespondAsync(object message) => throw Unsupported();

    public Task RespondAsync(object message, Type messageType) => throw Unsupported();

    public Task RespondAsync(object message, IPipe<SendContext> sendPipe) => throw Unsupported();

    public Task RespondAsync(object message, Type messageType, IPipe<SendContext> sendPipe) => throw Unsupported();

    public Task RespondAsync<T>(object values) where T : class => throw Unsupported();

    public Task RespondAsync<T>(object values, IPipe<SendContext<T>> sendPipe) where T : class => throw Unsupported();

    public Task RespondAsync<T>(object values, IPipe<SendContext> sendPipe) where T : class => throw Unsupported();

    // ISendEndpointProvider / IPublishEndpoint
    public Task<ISendEndpoint> GetSendEndpoint(Uri address) => throw Unsupported();

    public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw Unsupported();

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw Unsupported();

    public Task Publish<T>(T message, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();

    public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();

    public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();

    public Task Publish(object message, CancellationToken cancellationToken = default) => throw Unsupported();

    public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task Publish<T>(object values, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();

    public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();

    public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default)
        where T : class => throw Unsupported();
}
