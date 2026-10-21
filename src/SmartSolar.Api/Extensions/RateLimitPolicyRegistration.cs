using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Modules.Common.RateLimiting;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// A fixed-window policy whose limits come from <c>{Section}:PermitLimit</c> / <c>{Section}:WindowMinutes</c>.
/// <paramref name="Distributed"/> policies count cluster-wide in Redis when it is configured (expensive
/// operations); the others count per API instance.
/// </summary>
public sealed record RateLimitPolicyDefinition(string Name, string Section, int PermitLimit, int WindowMinutes, string? RouteValuePartition = null,
    bool Distributed = false);

/// <summary>
/// Registration shared by the Installation Flow, file and knowledge-base policies. Same conventions as
/// the existing ones: fixed window, no queueing, partitioned by the JWT subject (client IP only for
/// anonymous calls), enveloped 429 with Retry-After from <see cref="RateLimitingExtensions"/>.
/// Limits are per API instance; the cost-bearing operations are additionally guarded in PostgreSQL.
/// </summary>
public static partial class RateLimitingExtensions
{
    private static IServiceCollection AddFixedWindowPolicies(IServiceCollection services, IConfiguration configuration, IReadOnlyList<RateLimitPolicyDefinition> policies)
    {
        foreach (var policy in policies)
        {
            var section = configuration.GetSection(policy.Section);
            var limit = section.GetValue<int?>("PermitLimit") ?? policy.PermitLimit;
            var window = section.GetValue<int?>("WindowMinutes") ?? policy.WindowMinutes;
            if (limit < 1 || limit > 100_000 || window < 1 || window > 1440)
                throw new InvalidOperationException($"'{policy.Section}' must have PermitLimit 1-100000 and WindowMinutes 1-1440.");
        }

        services.AddRateLimiter(options =>
        {
            foreach (var policy in policies)
            {
                options.AddPolicy(policy.Name, context =>
                {
                    var section = configuration.GetSection(policy.Section);
                    var key = PartitionKey(context, policy);
                    var limit = section.GetValue<int?>("PermitLimit") ?? policy.PermitLimit;
                    var window = TimeSpan.FromMinutes(section.GetValue<int?>("WindowMinutes") ?? policy.WindowMinutes);
                    var counter = policy.Distributed ? context.RequestServices.GetService<IDistributedRateLimitCounter>() : null;
                    if (counter is not null)
                    {
                        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DistributedFixedWindowRateLimiter));
                        return RateLimitPartition.Get(key, k => new DistributedFixedWindowRateLimiter(counter, k, limit, window, logger));
                    }
                    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
                });
            }
        });
        return services;
    }

    /// <summary>User id (plus a route value such as the contract id when configured); never echoed to clients.</summary>
    private static string PartitionKey(HttpContext context, RateLimitPolicyDefinition policy)
    {
        var subject = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var owner = context.User.Identity?.IsAuthenticated == true && Guid.TryParse(subject, out var userId)
            ? $"user:{userId}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        var extra = policy.RouteValuePartition is { } name && context.Request.RouteValues.TryGetValue(name, out var value) ? $":{name}:{value}" : "";
        return $"{policy.Name}:{owner}{extra}";
    }
}
