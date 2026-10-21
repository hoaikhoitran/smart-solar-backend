using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.SystemSettings;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The real <see cref="SchedulingPolicyProvider"/> over the test database, without cache, with bootstrap defaults rebuilt from the
/// test context's option objects on every read: a test that changes <c>ctx.Options.AcceptanceWindowMinutes</c> (a default) or saves a
/// system_setting row (an Admin update) is seen by the next handler call, exactly as production sees an update after the cache TTL.
/// Reads are serialised: PostgreSQL race tests may share the seeding context's provider across racers.
/// </summary>
public sealed class TestSchedulingPolicyProvider(AppDbContext db, Func<SchedulingOptions> scheduling, Func<OnSiteSurveyOptions> onSite,
    Func<InstallationOptions> installation, TimeProvider time) : ISchedulingPolicyProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SchedulingPolicySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await new SchedulingPolicyProvider(new SystemSettingsUnitOfWork(db), SchedulingPolicyDefinitions.Create(scheduling(), onSite(), installation()),
                new SchedulingPolicyCache(TimeSpan.Zero), time).GetAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
    }
}
