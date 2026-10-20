using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Api.Background;

/// <summary>
/// Each run (Scheduling:EscalationPollSeconds, disabled with Scheduling:EscalationWorkerEnabled = false) first cancels open
/// survey appointments whose start passed without a confirmed technician (NOT_ACCEPTED_BEFORE_START; the request returns to
/// Pending so the customer can book again), then escalates PENDING_ACCEPTANCE appointments whose acceptance deadline passed and
/// that have not started. Every change is a conditional update, so several API instances and concurrent acceptances never
/// conflict, and one failing appointment is skipped (logged by id) without aborting the batch. Queries already treat overdue
/// pending appointments as escalated and started open ones as cancelled, so a stopped worker only delays the events, the Sales
/// notifications and the release of the request for a new booking (a new booking of the request cancels it first anyway).
/// Finally it records acceptance reminders (<c>scheduling.acceptanceReminderLeadMinutes</c>, 0 = disabled; one per acceptance window).
/// The settings snapshot is read on every run; deadlines are the persisted ones.
/// </summary>
public sealed class AppointmentEscalationWorker(
    IServiceScopeFactory scopes,
    SchedulingOptions options,
    ILogger<AppointmentEscalationWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.EscalationPollSeconds)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database unreachable and similar: try again on the next tick.
                logger.LogWarning(ex, "Appointment escalation sweep failed; retrying on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One sweep; returns the number of appointments cancelled, escalated and reminded.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AppointmentEscalationService>();
        var cancelled = await service.CancelNotAcceptedBeforeStartAsync(BatchSize, cancellationToken);
        if (cancelled > 0) logger.LogInformation("Cancelled {Count} survey appointment(s) nobody accepted before their start.", cancelled);
        var escalated = await service.EscalateDueAsync(BatchSize, cancellationToken);
        if (escalated > 0) logger.LogInformation("Escalated {Count} survey appointment(s) past their acceptance deadline.", escalated);
        var reminded = await service.SendAcceptanceRemindersAsync(BatchSize, cancellationToken);
        if (reminded > 0) logger.LogInformation("Recorded {Count} acceptance reminder(s).", reminded);
        return cancelled + escalated + reminded;
    }
}
