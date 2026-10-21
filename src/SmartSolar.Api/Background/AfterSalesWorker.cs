using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Background;

/// <summary>
/// Runs the Flow 3 sweep every <c>AfterSales:PollSeconds</c> (default 300): checkout / quotation expiry after PayOS reconciliation, package
/// activation and expiry, maintenance notices, Sales follow-ups and acceptance reminders. Every step is idempotent, so running it on
/// several instances is safe (row revisions decide who wins).
/// </summary>
public sealed class AfterSalesWorker(IServiceScopeFactory scopes, FeatureOptions features, IConfiguration configuration, ILogger<AfterSalesWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue("AfterSales:PollSeconds", 300), 30, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!features.InstallationWorkflowEnabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<AfterSalesSweepService>().RunOnceAsync(stoppingToken);
                if (result.Total > 0) logger.LogInformation("After-sales sweep: {Result}", result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "After-sales sweep failed; retrying on the next tick.");
            }
        }
    }
}
