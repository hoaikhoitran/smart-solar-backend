using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSolar.Infrastructure.Persistence.SystemSettings;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Admin-editable timing settings. Bootstrap defaults come from the "Scheduling", "OnSiteSurvey" and "Installation" sections (and
/// code for the dispatch keys) and are validated at startup. The effective policy is cached per process for
/// <c>SystemSettings:CacheSeconds</c> (default and maximum 15 s).
/// </summary>
public static class SystemSettingsModuleExtensions
{
    public const string CacheSecondsKey = "SystemSettings:CacheSeconds";

    public static IServiceCollection AddSystemSettingsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var definitions = SchedulingPolicyDefinitions.Create(
            ConfigurationBinding.Bind<SchedulingOptions>(configuration, SchedulingOptions.SectionName),
            ConfigurationBinding.Bind<OnSiteSurveyOptions>(configuration, OnSiteSurveyOptions.SectionName),
            ConfigurationBinding.Bind<InstallationOptions>(configuration, InstallationOptions.SectionName));
        var errors = definitions.ValidateDefaults();
        if (errors.Count > 0) throw new InvalidOperationException("System settings defaults are invalid: " + string.Join(" ", errors));

        var cacheSeconds = configuration.GetValue(CacheSecondsKey, 15);
        if (cacheSeconds is < 0 or > 15) throw new InvalidOperationException($"'{CacheSecondsKey}' must be between 0 and 15.");

        services.AddSingleton(definitions);
        services.AddSingleton(new SchedulingPolicyCache(TimeSpan.FromSeconds(cacheSeconds)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISystemSettingsUnitOfWork, SystemSettingsUnitOfWork>();
        services.AddScoped<ISchedulingPolicyProvider, SchedulingPolicyProvider>();
        services.AddScoped<SystemSettingsService>();
        return services;
    }
}

/// <summary>System settings policies, per user and per instance like the other admin endpoints.</summary>
public static partial class RateLimitingExtensions
{
    public const string SystemSettingsReadPolicy = "system-settings-read";
    public const string SystemSettingsWritePolicy = "system-settings-write";

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> SystemSettingsPolicies =
    [
        new(SystemSettingsReadPolicy, "RateLimiting:SystemSettingsRead", 60, 1),
        new(SystemSettingsWritePolicy, "RateLimiting:SystemSettingsWrite", 10, 1),
    ];

    public static IServiceCollection AddSystemSettingsRateLimiting(this IServiceCollection services, IConfiguration configuration)
        => AddFixedWindowPolicies(services, configuration, SystemSettingsPolicies);
}
