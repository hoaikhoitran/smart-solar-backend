namespace SmartSolar.Modules.Payments;

/// <summary>
/// One PayOS checkout. Originally (and still) used to collect 100% of a signed contract's value; it may also carry after-sales
/// items (service packages, repair quotations), alone or together with the contract, as <see cref="PaymentAttemptItem"/> rows.
/// A contract has at most one payable (PENDING/PROCESSING) attempt and at most one PAID attempt (partial unique indexes).
/// Expired, cancelled or failed attempts are kept and a new attempt is created on retry. Only the verified webhook or a
/// server-to-server status query can mark an attempt PAID.
/// </summary>
public sealed class PaymentAttempt
{
    public Guid Id { get; set; }

    /// <summary>The installation contract paid by this checkout; null for an after-sales-only checkout.</summary>
    public Guid? ContractId { get; set; }
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

    /// <summary>CASH only: the Manager/Admin who recorded the cash receipt (who). <see cref="PaidAt"/> is when, <see cref="AmountPaid"/> how much,
    /// <see cref="ProviderReference"/> the receipt reference (unique per provider).</summary>
    public Guid? ConfirmedByUserId { get; set; }

    /// <summary>CASH only: verified PAYMENT_EVIDENCE file (receipt scan or photo) uploaded by the confirming staff member.</summary>
    public Guid? EvidenceFileId { get; set; }

    /// <summary>CASH only: free-text note of the confirmation.</summary>
    public string? ConfirmationNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// Payment methods of a contract. ONLINE = the configured gateway (PayOS); CASH = the customer pays in cash and a Manager/Admin records it.
/// A CASH attempt occupies the same "one payable attempt per contract" slot as an online link, so the two methods can never run at once.
/// </summary>
public static class PaymentProviders
{
    public const string Cash = "CASH";
}

/// <summary>How the customer pays (the <c>method</c> of a payment attempt view and of a package purchase).</summary>
public static class PaymentMethods
{
    public const string Online = "ONLINE";
    public const string Cash = "CASH";
}

/// <summary>Derived payment state of a contract for the FE (the contract status itself stays SIGNED until it is PAID).</summary>
public static class ContractPaymentStates
{
    /// <summary>The contract is not SIGNED yet (or was superseded): nothing can be paid.</summary>
    public const string NotPayable = "NOT_PAYABLE";
    /// <summary>SIGNED, no method in progress: the customer chooses ONLINE or CASH.</summary>
    public const string AwaitingPayment = "AWAITING_PAYMENT";
    /// <summary>An online (PayOS) link is open.</summary>
    public const string OnlineInProgress = "ONLINE_IN_PROGRESS";
    /// <summary>The customer chose CASH; a Manager/Admin must record the receipt. Not paid yet.</summary>
    public const string AwaitingCashConfirmation = "AWAITING_CASH_CONFIRMATION";
    public const string Paid = "PAID";
}

/// <summary>
/// Allocation of one checkout amount to the things it pays (one row per item). The items always add up to the attempt amount, so
/// a combined checkout (contract + packages) is settled once and reported per category without counting the total twice.
/// Contract-only attempts created before this table existed have no rows (their whole amount is the contract).
/// </summary>
public sealed class PaymentAttemptItem
{
    public Guid Id { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public string ItemType { get; set; } = null!;
    public Guid? ContractId { get; set; }
    public Guid? SubscriptionId { get; set; }
    /// <summary>After-sales (service) quotation paid by this line.</summary>
    public Guid? QuotationId { get; set; }
    /// <summary>
    /// Installation quotation agreed on site and paid BEFORE its contract exists (2026-11-18 technician-led flow): the contract is created from
    /// this quotation's snapshot when the line is settled. Exactly one of contract / subscription / quotation / installation quotation is set.
    /// </summary>
    public Guid? InstallationQuotationId { get; set; }
    /// <summary>Installment of a payment-plan contract paid by this line (2026-11-19: deposit 30% or balance 70%).</summary>
    public Guid? ContractInstallmentId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>PENDING until the payment is verified, then SETTLED, or REVIEW_REQUIRED when the money cannot be applied (manual refund).</summary>
    public string Status { get; set; } = null!;

    public string? Note { get; set; }
}

public static class PaymentItemTypes
{
    public const string Contract = "CONTRACT";
    public const string MaintenancePackage = "MAINTENANCE_PACKAGE";
    public const string DisasterPackage = "DISASTER_PACKAGE";
    public const string RepairQuotation = "REPAIR_QUOTATION";
    public const string MaintenanceQuotation = "MAINTENANCE_QUOTATION";
    /// <summary>An installation quotation agreed on site with the technician; settling it creates the contract (payment before contract).</summary>
    public const string InstallationQuotation = "INSTALLATION_QUOTATION";
    /// <summary>One installment (deposit / balance) of a payment-plan contract; the attempt carries no contract id (the line names the installment).</summary>
    public const string ContractInstallment = "CONTRACT_INSTALLMENT";
    public static readonly IReadOnlyList<string> All = [Contract, MaintenancePackage, DisasterPackage, RepairQuotation, MaintenanceQuotation, InstallationQuotation, ContractInstallment];
}

public static class PaymentItemStatuses
{
    public const string Pending = "PENDING";
    public const string Settled = "SETTLED";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public static readonly IReadOnlyList<string> All = [Pending, Settled, ReviewRequired];
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
    public Guid? ContractId { get; set; }
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
    /// <summary>A Manager/Admin action (cash confirmation).</summary>
    public const string Staff = "STAFF";
    /// <summary>The assigned technician opened the payment on site (QR shown on the technician's device, or cash chosen for a Manager to confirm).</summary>
    public const string Technician = "TECHNICIAN";
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
    public const string CheckoutInProgress = "PAYMENT_CHECKOUT_IN_PROGRESS";
    /// <summary>The customer chose CASH and it waits for a Manager/Admin; cancel the cash choice first to pay online (409).</summary>
    public const string CashSelected = "PAYMENT_CASH_SELECTED";
    /// <summary>There is no CASH payment awaiting confirmation for this contract (409).</summary>
    public const string CashNotSelected = "PAYMENT_CASH_NOT_SELECTED";
    /// <summary>The confirmed cash amount is not exactly the contract total (no partial or over-payment is recorded) (409).</summary>
    public const string CashAmountMismatch = "PAYMENT_CASH_AMOUNT_MISMATCH";
    /// <summary>The receipt reference was already used by another cash confirmation (409).</summary>
    public const string CashReceiptDuplicate = "PAYMENT_CASH_RECEIPT_DUPLICATE";
    /// <summary>A line of the cash payment can no longer be applied (quotation expired, rejected or replaced; package order released): nothing recorded (409).</summary>
    public const string CashItemNotPayable = "PAYMENT_CASH_ITEM_NOT_PAYABLE";
    /// <summary>The contract is paid in installments (30/70 plan): use the installment payment endpoints, never a 100% contract payment (409).</summary>
    public const string ContractHasInstallments = "PAYMENT_CONTRACT_HAS_INSTALLMENTS";
}
