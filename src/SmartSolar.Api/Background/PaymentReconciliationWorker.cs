using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Api.Background;

/// <summary>
/// Reconciles payable attempts with PayOS (server-to-server) so a lost or delayed webhook never leaves a paid contract unpaid, and expired links are
/// closed. Every <c>Payments:ReconciliationIntervalSeconds</c> (30 s) it asks PayOS about each attempt not checked for
/// <c>Payments:ReconcileAfterSeconds</c> (60 s), draining all due attempts in batches; confirmations are idempotent with the webhook path. Worst case
/// for a lost webhook ≈ 90 s + the provider call (NFR: PAID within 2 minutes).
/// </summary>
public sealed class PaymentReconciliationWorker(IServiceScopeFactory scopes, FeatureOptions features, PaymentOptions options, ILogger<PaymentReconciliationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.ReconciliationIntervalSeconds, 5, 600)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!features.InstallationWorkflowEnabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PaymentService>().ReconcileDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Payment reconciliation sweep failed; retrying on the next tick.");
            }
        }
    }
}
