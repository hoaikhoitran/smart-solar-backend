using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Api.Background;

/// <summary>
/// Retries password-reset emails whose publish failed (broker unavailable) and expires the ones whose token ran out. Safe on several
/// instances: each row is claimed with a conditional update before it is published.
/// </summary>
public sealed class PasswordResetDispatchWorker(IServiceScopeFactory scopes, ILogger<PasswordResetDispatchWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var (published, _) = await scope.ServiceProvider.GetRequiredService<PasswordResetDispatcher>().SweepAsync(BatchSize, stoppingToken);
                if (published > 0) logger.LogInformation("Published {Count} pending password reset email(s).", published);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Password reset dispatch sweep failed; retrying on the next tick.");
            }
        }
    }
}
