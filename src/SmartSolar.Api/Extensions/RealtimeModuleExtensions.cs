using Microsoft.AspNetCore.SignalR;
using SmartSolar.Api.Realtime;
using SmartSolar.Infrastructure.Persistence.Realtime;
using SmartSolar.Modules.Common.Realtime;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Optional SignalR realtime layer (2026-11-28). Disabled (default): only a no-op publisher is registered — no hub, no endpoint, no EF interceptor,
/// so the application behaves exactly as before. Enabled: in-process SignalR (single API instance; see the report for the optional Redis backplane),
/// the hub at <see cref="RealtimeHub.Path"/> and the post-commit EF interceptor that publishes committed notification rows.
/// </summary>
public static class RealtimeModuleExtensions
{
    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
        options.Validate();
        services.AddSingleton(options);
        if (!options.Enabled)
        {
            services.AddSingleton<IRealtimeNotificationPublisher, NoOpRealtimeNotificationPublisher>();
            return services;
        }

        services.AddSignalR(hub =>
        {
            hub.EnableDetailedErrors = false;
            // The hub has no client-invokable method: a tiny receive limit is enough (handshake / pings).
            hub.MaximumReceiveMessageSize = 4 * 1024;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            hub.MaximumParallelInvocationsPerClient = 1;
        });
        services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
        services.AddSingleton<IRealtimeNotificationPublisher, SignalRRealtimePublisher>();
        services.AddSingleton<RealtimeNotificationInterceptor>();
        return services;
    }

    public static WebApplication MapRealtime(this WebApplication app)
    {
        if (app.Services.GetRequiredService<RealtimeOptions>().Enabled)
            app.MapHub<RealtimeHub>(RealtimeHub.Path, hub => hub.CloseOnAuthenticationExpiration = true).RequireAuthorization();
        return app;
    }
}
