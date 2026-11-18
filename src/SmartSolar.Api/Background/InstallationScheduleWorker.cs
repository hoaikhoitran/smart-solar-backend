using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Api.Background;

/// <summary>
/// Safety net for technician-agreed installation schedules: confirms the PLANNED schedule of every PAID contract whose post-payment
/// follow-up did not run or failed (crash, transient conflict), and marks PLANNED schedules whose start passed before payment as
/// RESCHEDULE_REQUIRED (so a stale date is never shown as current). Idempotent with the payment follow-up. Also opens collection cases for overdue
/// 30/70 installments (one open case per installment, unique index).
/// </summary>
public sealed class InstallationScheduleWorker(IServiceScopeFactory scopes, FeatureOptions features, ILogger<InstallationScheduleWorker> logger) : BackgroundService
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
                var (confirmed, expired) = await scope.ServiceProvider.GetRequiredService<InstallationScheduleHandlers>().SweepAsync(25, stoppingToken);
                if (confirmed + expired > 0)
                    logger.LogInformation("Installation schedule sweep: {Confirmed} confirmed, {Expired} need a new date.", confirmed, expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Installation schedule sweep failed; retrying on the next tick.");
            }
            // 30/70 plan (2026-11-19): overdue installments get a collection case for Sales (no automatic money or asset action).
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SmartSolar.Modules.CustomerContracts.PaymentPlanLedger>().SweepOverdueAsync(50, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Overdue installment sweep failed; retrying on the next tick.");
            }
        }
    }
}
