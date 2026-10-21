using SmartSolar.Modules.Scheduling.Dispatch;

namespace SmartSolar.Api.Background;

/// <summary>Configuration section "Dispatch" (infrastructure only: the business durations are system settings).</summary>
public sealed class DispatchWorkerOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>Runs the background sweep (proposal expiry, overdue marking, Admin escalation, reminders).</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>How often the sweep runs.</summary>
    public int PollSeconds { get; set; } = 60;
}

/// <summary>
/// Each run (Dispatch:PollSeconds, disabled with Dispatch:WorkerEnabled = false) sweeps uncovered-zone dispatch requests with
/// <see cref="DispatchSweepService"/>: expires PROPOSED proposals whose persisted expires_at passed (the request returns to OPEN), marks
/// OPEN requests overdue at their persisted review_due_at, escalates them to Admin once at their persisted escalation_due_at, and
/// records reminders at the persisted interval. It never resolves, closes or expires a dispatch request, and never touches an
/// appointment. Every change is conditional, so several instances can run it concurrently.
/// </summary>
public sealed class DispatchWorker(IServiceScopeFactory scopes, DispatchWorkerOptions options, ILogger<DispatchWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dispatch sweep failed; retrying on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<DispatchSweepService.SweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DispatchSweepService>().RunOnceAsync(BatchSize, cancellationToken);
        if (result.Total > 0)
            logger.LogInformation("Dispatch sweep: {Expired} proposal(s) expired, {Overdue} request(s) overdue, {Escalated} escalated, {Reminders} reminder(s).",
                result.ExpiredProposals, result.MarkedOverdue, result.Escalated, result.Reminders);
        return result;
    }
}
