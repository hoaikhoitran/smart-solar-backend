using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Api.Background;

/// <summary>
/// Reconciles payable attempts with PayOS (server-to-server) so a lost or delayed webhook never
/// leaves a paid contract unpaid, and expired links are closed. Attempts are re-checked at most
/// every two minutes; confirmations are idempotent with the webhook path.
/// </summary>
public sealed class PaymentReconciliationWorker(IServiceScopeFactory scopes, FeatureOptions features, TimeProvider time, ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!features.InstallationWorkflowEnabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IPaymentUnitOfWork>();
                var service = scope.ServiceProvider.GetRequiredService<PaymentService>();
                foreach (var id in await unitOfWork.ListAttemptsToReconcileAsync(time.GetUtcNow().AddMinutes(-2), 25, stoppingToken))
                    await service.ReconcileAsync(id, force: true, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Payment reconciliation sweep failed; retrying on the next tick.");
            }
        }
    }
}
