namespace SmartSolar.Api.Extensions;

/// <summary>Installation Flow and shared file-reference policies.</summary>
public static partial class RateLimitingExtensions
{
    public const string FilesReadPolicy = "files-read";
    public const string FilesWritePolicy = "files-write";
    public const string OnSiteSurveyReadPolicy = "onsite-survey-read";
    public const string OnSiteSurveyWritePolicy = "onsite-survey-write";
    public const string QuotationReadPolicy = "quotation-read";
    public const string QuotationWritePolicy = "quotation-write";
    public const string ContractReadPolicy = "contract-read";
    public const string ContractWritePolicy = "contract-write";
    public const string PaymentCreatePolicy = "payment-create";
    public const string PaymentStatusPolicy = "payment-status";
    public const string InstallationReadPolicy = "installation-read";
    public const string InstallationWritePolicy = "installation-write";
    public const string InstallationSchedulingPolicy = "installation-scheduling";
    public const string PayOsWebhookPolicy = "payos-webhook";
    public const string InstallationPreviewPolicy = "installation-preview";

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> InstallationWorkflowPolicies =
    [
        new(FilesReadPolicy, "RateLimiting:FilesRead", 60, 1),
        new(FilesWritePolicy, "RateLimiting:FilesWrite", 20, 1, Distributed: true),
        new(OnSiteSurveyReadPolicy, "RateLimiting:OnSiteSurveyRead", 60, 1),
        new(OnSiteSurveyWritePolicy, "RateLimiting:OnSiteSurveyWrite", 20, 1),
        new(QuotationReadPolicy, "RateLimiting:QuotationRead", 60, 1),
        new(QuotationWritePolicy, "RateLimiting:QuotationWrite", 20, 1),
        new(ContractReadPolicy, "RateLimiting:ContractRead", 60, 1),
        new(ContractWritePolicy, "RateLimiting:ContractWrite", 10, 1),
        new(PaymentCreatePolicy, "RateLimiting:PaymentCreate", 5, 10, RouteValuePartition: "contractId", Distributed: true),
        new(PaymentStatusPolicy, "RateLimiting:PaymentStatus", 30, 1),
        new(InstallationReadPolicy, "RateLimiting:InstallationRead", 90, 1),
        new(InstallationWritePolicy, "RateLimiting:InstallationWrite", 20, 1),
        new(InstallationSchedulingPolicy, "RateLimiting:InstallationScheduling", 10, 1, Distributed: true),
        new(InstallationPreviewPolicy, "RateLimiting:InstallationPreview", 15, 1, Distributed: true),
        // Anonymous abuse guard per source IP (forwarded IP only from configured proxies). Far above PayOS'
        // retry rate, so legitimate deliveries and retries are never throttled; customer limits never apply.
        new(PayOsWebhookPolicy, "RateLimiting:PayOsWebhook", 600, 1),
    ];

    public static IServiceCollection AddInstallationWorkflowRateLimiting(this IServiceCollection services, IConfiguration configuration)
        => AddFixedWindowPolicies(services, configuration, InstallationWorkflowPolicies);
}
