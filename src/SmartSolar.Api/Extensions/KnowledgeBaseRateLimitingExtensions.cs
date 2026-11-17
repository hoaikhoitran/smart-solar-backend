namespace SmartSolar.Api.Extensions;

/// <summary>Knowledge base (RAG chatbot) policies. Questions are additionally bounded by the PostgreSQL daily token/request ledger.</summary>
public static partial class RateLimitingExtensions
{
    public const string RagChatPolicy = "rag-chat";
    public const string RagHistoryPolicy = "rag-history";
    public const string RagAdminReadPolicy = "rag-admin-read";
    public const string RagAdminRegisterPolicy = "rag-admin-register";
    public const string RagAdminReindexPolicy = "rag-admin-reindex";
    public const string RagAdminPublishPolicy = "rag-admin-publish";
    public const string RagAdminConfigPolicy = "rag-admin-config";

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> KnowledgeBasePolicies =
    [
        // 5 questions per user per minute across all replicas (Redis); the PostgreSQL admission adds the daily quota, concurrency and USD budget.
        new(RagChatPolicy, "RateLimiting:RagChat", 5, 1, Distributed: true),
        new(RagHistoryPolicy, "RateLimiting:RagHistory", 60, 1),
        new(RagAdminReadPolicy, "RateLimiting:RagAdminRead", 60, 1),
        new(RagAdminRegisterPolicy, "RateLimiting:RagAdminRegister", 10, 1, Distributed: true),
        new(RagAdminReindexPolicy, "RateLimiting:RagAdminReindex", 3, 10, Distributed: true),
        new(RagAdminPublishPolicy, "RateLimiting:RagAdminPublish", 10, 1),
        new(RagAdminConfigPolicy, "RateLimiting:RagAdminConfig", 10, 1, Distributed: true),
    ];

    public static IServiceCollection AddKnowledgeBaseRateLimiting(this IServiceCollection services, IConfiguration configuration)
        => AddFixedWindowPolicies(services, configuration, KnowledgeBasePolicies);
}
