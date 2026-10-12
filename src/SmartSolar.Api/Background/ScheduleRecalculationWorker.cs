using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Api.Background;

/// <summary>
/// Retries durable route recalculation requests (technician_schedule_recalculation). Requests are
/// written in the same transaction as the business change and normally completed inline right
/// after it; this worker picks up whatever is left (provider outage, crash, restart). Each request
/// is leased in the database, so several API instances never process the same technician-day at once.
/// </summary>
public sealed class ScheduleRecalculationWorker(
    IServiceScopeFactory scopes,
    SchedulingOptions options,
    TimeProvider time,
    ILogger<ScheduleRecalculationWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.RecalculationPollSeconds)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database unreachable and similar: try again on the next tick.
                logger.LogWarning(ex, "Route recalculation sweep failed; retrying on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid TechnicianUserId, DateOnly LocalDate)> due;
        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<ISchedulingUnitOfWork>()
                .ListDueRecalculationsAsync(time.GetUtcNow(), BatchSize, cancellationToken);
        }

        foreach (var (technicianUserId, localDate) in due)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ScheduleRecalculationService>().TryRunAsync(technicianUserId, localDate, cancellationToken);
        }
        return due.Count;
    }
}
