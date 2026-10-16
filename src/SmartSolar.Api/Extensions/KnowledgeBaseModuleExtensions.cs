using SmartSolar.Infrastructure.Persistence.KnowledgeBase;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Knowledge base (RAG chatbot): document lifecycle, durable ingestion, chat and cost quotas, plus
/// the signed client of the internal FastAPI service. Independent of the Installation Flow; requires
/// <see cref="FileStorageModuleExtensions.AddFeatureFlagsAndFileStorage"/> for document files.
/// </summary>
public static class KnowledgeBaseModuleExtensions
{
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services, IConfiguration configuration)
    {
        var rag = ConfigurationBinding.Bind<SmartSolar.Modules.KnowledgeBase.RagOptions>(configuration, SmartSolar.Modules.KnowledgeBase.RagOptions.SectionName);
        if (rag.Enabled)
        {
            if (string.IsNullOrWhiteSpace(rag.ServiceSharedSecret) || rag.ServiceSharedSecret.Length < 32)
                throw new InvalidOperationException("'Rag:ServiceSharedSecret' must be at least 32 characters when 'Rag:Enabled' is true.");
            if (!Uri.TryCreate(rag.ServiceBaseUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("'Rag:ServiceBaseUrl' must be an absolute URL when 'Rag:Enabled' is true.");
        }
        services.AddSingleton(rag);
        services.AddHttpClient<SmartSolar.Modules.KnowledgeBase.IRagServiceClient, SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient>(client =>
        {
            client.BaseAddress = new Uri(rag.ServiceBaseUrl.TrimEnd('/') + "/");
            // Each call has its own deadline (RagServiceClient); this outer bound only catches a stuck socket.
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(Math.Max(rag.RequestTimeoutSeconds, rag.IngestTimeoutSeconds), 1, 1800) + 30);
        }).RemoveAllLoggers();
        services.AddScoped<SmartSolar.Modules.KnowledgeBase.IKnowledgeUnitOfWork, KnowledgeUnitOfWork>();
        services.AddScoped<SmartSolar.Modules.KnowledgeBase.KnowledgeDocumentHandlers>();
        services.AddScoped<SmartSolar.Modules.KnowledgeBase.KnowledgeIngestionService>();
        services.AddScoped<SmartSolar.Modules.KnowledgeBase.ChatService>();
        if (rag.IngestionWorkerEnabled)
            services.AddHostedService<Background.KnowledgeIngestionWorker>();

        return services;
    }
}
