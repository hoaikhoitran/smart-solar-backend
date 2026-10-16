namespace SmartSolar.Modules.Payments;

/// <summary>
/// One attempt to collect 100% of a signed contract's value through PayOS. A contract has at most
/// one payable (PENDING/PROCESSING) attempt and at most one PAID attempt (both enforced by partial
/// unique indexes). Expired, cancelled or failed attempts are kept and a new attempt is created on retry.
/// Only the verified webhook or a server-to-server status query can mark an attempt PAID.
/// </summary>
public sealed class PaymentAttempt
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid CustomerUserId { get; set; }
    public string Provider { get; set; } = null!;
    public long OrderCode { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? ProviderPaymentLinkId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string? QrCode { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ProviderReference { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTimeOffset? ProviderTransactionAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>Every webhook delivery (deduplicated by payload hash). Only the fields needed for audit are kept.</summary>
public sealed class PaymentWebhookEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = null!;
    public string PayloadSha256 { get; set; } = null!;
    public long? OrderCode { get; set; }
    public string? ProviderReference { get; set; }
    public decimal? Amount { get; set; }
    public string? Code { get; set; }
    public bool SignatureValid { get; set; }
    public string Outcome { get; set; } = null!;
    public string? Detail { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Payment history (status changes of attempts, confirmations, reconciliations).</summary>
public sealed class PaymentEvent
{
    public Guid Id { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public Guid ContractId { get; set; }
    public string EventType { get; set; } = null!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string Source { get; set; } = null!;
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Paid = "PAID";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
    public const string Failed = "FAILED";
    /// <summary>Money arrived but does not match the contract (wrong amount): manual handling, contract not paid.</summary>
    public const string ReviewRequired = "REVIEW_REQUIRED";
    /// <summary>A second successful payment for an already paid contract: must be refunded manually.</summary>
    public const string RefundRequired = "REFUND_REQUIRED";

    public static readonly IReadOnlyList<string> All = [Pending, Processing, Paid, Cancelled, Expired, Failed, ReviewRequired, RefundRequired];
    public static readonly IReadOnlyList<string> Payable = [Pending, Processing];
}

public static class WebhookOutcomes
{
    public const string Applied = "APPLIED";
    public const string Duplicate = "DUPLICATE";
    public const string Ignored = "IGNORED";
    public const string Rejected = "REJECTED";
}

public static class PaymentSources
{
    public const string Webhook = "WEBHOOK";
    public const string Reconciliation = "RECONCILIATION";
    public const string Customer = "CUSTOMER";
    public const string System = "SYSTEM";
}

public static class PaymentErrorCodes
{
    public const string ContractNotFound = "PAYMENT_CONTRACT_NOT_FOUND";
    public const string ContractNotSigned = "PAYMENT_CONTRACT_NOT_SIGNED";
    public const string AlreadyPaid = "PAYMENT_ALREADY_PAID";
    public const string AmountNotSupported = "PAYMENT_AMOUNT_NOT_SUPPORTED";
    public const string TooManyAttempts = "PAYMENT_TOO_MANY_ATTEMPTS";
    public const string ProviderNotConfigured = "PAYMENT_PROVIDER_NOT_CONFIGURED";
    public const string ProviderError = "PAYMENT_PROVIDER_ERROR";
    public const string AttemptNotFound = "PAYMENT_ATTEMPT_NOT_FOUND";
    public const string InvalidSignature = "PAYMENT_WEBHOOK_INVALID_SIGNATURE";
}
