using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Api.Background;

/// <summary>
/// Drains due knowledge ingestion / vector deletion jobs. Jobs are leased with an atomic
/// conditional update, so several API instances can run this worker without double processing;
/// a crashed lease expires and the job is picked up again.
/// </summary>
public sealed class KnowledgeIngestionWorker(IServiceScopeFactory scopes, FeatureOptions features, RagOptions options, ILogger<KnowledgeIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.IngestionPollSeconds, 5, 600)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!features.KnowledgeBaseEnabled || !options.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>().RunDueAsync(5, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Knowledge ingestion sweep failed; retrying on the next tick.");
            }
        }
    }
}
