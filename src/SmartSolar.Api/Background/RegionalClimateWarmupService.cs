using SmartSolar.Modules.SolarSimulation.Regional;

namespace SmartSolar.Api.Background;

/// <summary>
/// Startup warm-up of the configuration-independent 2025 HCMC regional climate aggregate (NASA POWER at every regional sample), so the
/// first simulation does not spend its provider budget on it. Best effort: readiness does not depend on it, a failure is only logged
/// (simulations then compute it within their own budget, or report the climate context as FAILED — never as zero).
/// </summary>
public sealed class RegionalClimateWarmupService(IServiceScopeFactory scopes, ILogger<RegionalClimateWarmupService> logger) : BackgroundService
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the host finish starting before any outbound call.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(Budget);
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IRegionalSolarBaselineProvider>();
            var started = DateTimeOffset.UtcNow;
            var result = await provider.GetClimateBaselineAsync(timeout.Token);
            if (result.Succeeded)
                logger.LogInformation("Regional climate baseline ready: {Succeeded}/{Total} samples, quality {Quality}, {Ms} ms.",
                    result.Coverage!.SucceededSampleCount, result.Coverage.SampleCount, result.Coverage.Quality, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
            else
                logger.LogWarning("Regional climate baseline warm-up did not complete: {Code}.", result.Failure?.Code);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Regional climate baseline warm-up failed.");
        }
    }
}
