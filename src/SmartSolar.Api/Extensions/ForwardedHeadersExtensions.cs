using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SmartSolar.Api.Extensions;

public static class ForwardedHeadersExtensions
{
    /// <summary>
    /// Behind a proxy the socket address is the proxy's, which would put every
    /// client in one rate-limit partition. X-Forwarded-For / X-Forwarded-Proto are honoured only when the
    /// connection comes from a proxy listed in configuration (<c>ForwardedHeaders:KnownProxies</c> addresses or
    /// <c>ForwardedHeaders:KnownNetworks</c> CIDR ranges, e.g. the Docker network of the TLS proxy), never blindly.
    /// Only the last hop is used (ForwardLimit 1), so a client cannot prepend fake addresses.
    /// Optional <c>HttpsRedirection:HttpsPort</c> (e.g. 443) makes plain-HTTP requests that did not come through
    /// the TLS proxy redirect to HTTPS.
    /// </summary>
    public static IServiceCollection AddForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var knownProxies = configuration
            .GetSection("ForwardedHeaders:KnownProxies")
            .Get<string[]>() ?? Array.Empty<string>();
        var knownNetworks = configuration
            .GetSection("ForwardedHeaders:KnownNetworks")
            .Get<string[]>() ?? Array.Empty<string>();
        var invalid = knownProxies.Where(p => !string.IsNullOrWhiteSpace(p) && !IPAddress.TryParse(p.Trim(), out _))
            .Concat(knownNetworks.Where(n => !string.IsNullOrWhiteSpace(n) && TryParseNetwork(n) is null)).ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException($"Invalid ForwardedHeaders:KnownProxies/KnownNetworks entries: {string.Join(", ", invalid)}.");

        // ForwardedHeadersMiddleware skips the source check when both lists are empty and would then accept X-Forwarded-For from ANY
        // client (spoofed IPs → a fresh rate-limit bucket per request). Without a configured trusted proxy, forwarding is disabled.
        var trustsAProxy = knownProxies.Any(p => !string.IsNullOrWhiteSpace(p)) || knownNetworks.Any(n => !string.IsNullOrWhiteSpace(n));

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = trustsAProxy ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            options.ForwardLimit = 1;

            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy?.Trim(), out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (var network in knownNetworks)
            {
                if (TryParseNetwork(network) is { } parsed)
                {
                    options.KnownNetworks.Add(parsed);
                }
            }
        });

        if (configuration.GetValue<int?>("HttpsRedirection:HttpsPort") is { } httpsPort)
        {
            services.AddHttpsRedirection(options => options.HttpsPort = httpsPort);
        }

        return services;
    }

    /// <summary>"172.30.250.0/24" → network; null when it is not a valid CIDR.</summary>
    public static Microsoft.AspNetCore.HttpOverrides.IPNetwork? TryParseNetwork(string? value)
    {
        var parts = value?.Trim().Split('/');
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length)) return null;
        var max = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return length < 0 || length > max ? null : new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length);
    }
}
