using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SmartSolar.Api.Extensions;

public static class ForwardedHeadersExtensions
{
    /// <summary>
    /// Behind a proxy the socket address is the proxy's, which would put every
    /// client in one rate-limit partition. X-Forwarded-For is honoured only for
    /// proxies listed in configuration, never blindly.
    /// </summary>
    public static IServiceCollection AddForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var knownProxies = configuration
            .GetSection("ForwardedHeaders:KnownProxies")
            .Get<string[]>() ?? Array.Empty<string>();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }
        });

        return services;
    }
}
