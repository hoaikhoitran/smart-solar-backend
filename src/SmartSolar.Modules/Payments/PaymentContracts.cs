using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Payments;

public sealed record PaymentLinkRequest(long OrderCode, long Amount, string Description, string ReturnUrl, string CancelUrl, DateTimeOffset ExpiresAt, string? BuyerName, string? BuyerEmail, string? BuyerPhone);

public enum GatewayCallStatus { Succeeded = 1, NotConfigured = 2, Rejected = 3, Unavailable = 4 }

public sealed record PaymentLinkResult(GatewayCallStatus Status, string? PaymentLinkId, string? CheckoutUrl, string? QrCode, string? Error);

/// <summary>Server-to-server view of a payment link (trusted: authenticated provider API over TLS).</summary>
public sealed record ProviderPaymentStatus(GatewayCallStatus CallStatus, string? Status, long? Amount, long? AmountPaid, string? Reference, DateTimeOffset? TransactionAt, string? Error);

/// <summary>Verified webhook content. <see cref="SignatureValid"/> false means nothing else may be trusted.</summary>
public sealed record WebhookNotification(bool SignatureValid, bool Success, string? Code, long? OrderCode, long? Amount, string? Reference,
    string? Currency, DateTimeOffset? TransactionAt, string? PaymentLinkId, string? Error);

/// <summary>Payment provider port. PayOS today; the domain never depends on PayOS types.</summary>
public interface IPaymentGateway
{
    string ProviderName { get; }
    Task<PaymentLinkResult> CreatePaymentLinkAsync(PaymentLinkRequest request, CancellationToken cancellationToken);
    Task<ProviderPaymentStatus> GetPaymentStatusAsync(long orderCode, CancellationToken cancellationToken);
    Task<GatewayCallStatus> CancelPaymentLinkAsync(long orderCode, string reason, CancellationToken cancellationToken);

    /// <summary>Parses and verifies a raw webhook body (signature over the data object). Never throws for bad input.</summary>
    WebhookNotification VerifyWebhook(string rawBody);
}

public sealed record PayableContract(Guid ContractId, string ContractNumber, Guid CustomerUserId, string Status, decimal TotalAmount, string Currency,
    string CustomerName, string? CustomerEmail, string? CustomerPhone);

public interface IPaymentUnitOfWork : ITransactionalUnitOfWork
{
    Task<PayableContract?> GetContractAsync(Guid contractId, CancellationToken cancellationToken);
    Task<PaymentAttempt?> FindAttemptAsync(Guid attemptId, bool track, CancellationToken cancellationToken);
    Task<PaymentAttempt?> FindByOrderCodeAsync(long orderCode, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentAttempt>> ListAttemptsAsync(Guid contractId, CancellationToken cancellationToken);
    Task<int> CountAttemptsSinceAsync(Guid contractId, DateTimeOffset since, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListAttemptsToReconcileAsync(DateTimeOffset checkedBefore, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken);
    Task<bool> WebhookSeenAsync(string payloadSha256, CancellationToken cancellationToken);

    /// <summary>Contract SIGNED → PAID (conditional); false when it was not SIGNED.</summary>
    Task<bool> TryMarkContractPaidAsync(Guid contractId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsContractPaidAsync(Guid contractId, CancellationToken cancellationToken);

    void AddAttempt(PaymentAttempt attempt);
    void AddEvent(PaymentEvent paymentEvent);
    void AddWebhookEvent(PaymentWebhookEvent webhookEvent);
}
