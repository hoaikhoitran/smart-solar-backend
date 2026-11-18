namespace SmartSolar.Api.Extensions;

/// <summary>
/// Policies for the customer / Sales portal endpoints that had none before the 2026-11-18 remediation (customer profile, sites, pre-surveys,
/// survey-request reads, simulation reads, the Sales queue) and for the retired 410 routes. Per user (per IP when anonymous), per instance:
/// these are cheap database reads and small writes; the expensive operations behind them (simulation creation, booking) keep their own
/// stricter policies. The test EndpointRateLimitCoverageTests fails when a controller action has no policy at all.
/// </summary>
public static partial class RateLimitingExtensions
{
    /// <summary>Portal reads (lists and details of the caller's own records, the Sales queue): 60 / min per user.</summary>
    public const string PortalReadPolicy = "portal-read";
    /// <summary>Portal writes (profile, sites, pre-survey create / update / submit / surface): 20 / min per user.</summary>
    public const string PortalWritePolicy = "portal-write";
    /// <summary>Retired routes (410 ENDPOINT_RETIRED): bounded so an outdated client loop cannot flood the logs.</summary>
    public const string RetiredEndpointPolicy = "retired-endpoint";

    public static readonly IReadOnlyList<RateLimitPolicyDefinition> PortalPolicies =
    [
        new(PortalReadPolicy, "RateLimiting:PortalRead", 60, 1),
        new(PortalWritePolicy, "RateLimiting:PortalWrite", 20, 1),
        new(RetiredEndpointPolicy, "RateLimiting:RetiredEndpoint", 30, 1),
    ];

    public static IServiceCollection AddPortalRateLimiting(this IServiceCollection services, IConfiguration configuration)
        => AddFixedWindowPolicies(services, configuration, PortalPolicies);
}
