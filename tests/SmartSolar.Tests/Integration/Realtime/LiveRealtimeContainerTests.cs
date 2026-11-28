using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Realtime;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Realtime;

/// <summary>
/// Opt-in: runs against a disposable API container started from the production image with Realtime__Enabled=true:
/// <c>SMARTSOLAR_TEST_REALTIME=http://127.0.0.1:18080|&lt;postgres connection string of that container's database&gt;|&lt;Jwt signing key&gt;[|&lt;container name to restart&gt;]</c>.
/// </summary>
public sealed class RealtimeLiveFactAttribute : FactAttribute
{
    public const string Variable = "SMARTSOLAR_TEST_REALTIME";

    public RealtimeLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to 'baseUrl|postgresConnection|jwtSigningKey[|containerName]' of a disposable realtime-enabled API container to run.";
    }
}

public sealed class LiveRealtimeContainerTests
{
    private static (Uri Base, string Database, string Key, string? Container) Settings()
    {
        var parts = Environment.GetEnvironmentVariable(RealtimeLiveFactAttribute.Variable)!.Split('|');
        return (new Uri(parts[0].TrimEnd('/') + "/"), parts[1], parts[2], parts.Length > 3 ? parts[3] : null);
    }

    private static string Token(string key, UserAccount user, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("SmartSolar", "SmartSolarClients",
        [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim("av", user.AuthVersion.ToString()), new Claim(ClaimTypes.Role, role)],
        DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));

    private static async Task<(UserAccount User, Guid First, Guid Second)> SeedCustomerAsync(string connection, string name)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var user = PreSurveyTestContext.NewUser(name, email: $"rt-{Guid.NewGuid():N}@example.com");
        db.UserAccounts.Add(user);
        var role = await db.Roles.SingleAsync(r => r.Code == RoleCodes.Customer);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        var first = AfterSalesHistory.NewNotification(user.Id, "RT_LIVE_1", "live 1", DateTimeOffset.UtcNow);
        var second = AfterSalesHistory.NewNotification(user.Id, "RT_LIVE_2", "live 2", DateTimeOffset.UtcNow);
        db.AfterSalesNotifications.AddRange(first, second);
        await db.SaveChangesAsync();
        return (user, first.Id, second.Id);
    }

    private static async Task<(HubConnection Connection, Channel<RealtimeEnvelope> Events)> ConnectAsync(Uri baseUri, string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/realtime"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;   // real WebSocket upgrade through Kestrel in the container
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)])
            .Build();
        var events = Channel.CreateUnbounded<RealtimeEnvelope>();
        connection.On<RealtimeEnvelope>("realtime", e => events.Writer.TryWrite(e));
        await connection.StartAsync();
        return (connection, events);
    }

    private static async Task<RealtimeEnvelope> NextAsync(Channel<RealtimeEnvelope> events, Func<RealtimeEnvelope, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await events.Reader.ReadAsync(timeout.Token);
            if (match(e)) return e;
        }
    }

    [RealtimeLiveFact]
    public async Task Container_websocket_delivery_is_authenticated_user_targeted_and_survives_an_api_restart()
    {
        var (baseUri, database, key, container) = Settings();
        using var http = new HttpClient { BaseAddress = baseUri };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("hubs/realtime/negotiate?negotiateVersion=1", null)).StatusCode);

        var (a, aFirst, aSecond) = await SeedCustomerAsync(database, "Live customer A");
        var (b, _, _) = await SeedCustomerAsync(database, "Live customer B");
        var tokenA = Token(key, a, RoleCodes.Customer);
        var (connectionA, eventsA) = await ConnectAsync(baseUri, tokenA);
        var (connectionB, eventsB) = await ConnectAsync(baseUri, Token(key, b, RoleCodes.Customer));
        await using var _a = connectionA;
        await using var _b = connectionB;
        // Transports = WebSockets only: a successful start proves the WebSocket upgrade worked (no long-polling fallback allowed here).
        Assert.Equal(HubConnectionState.Connected, connectionA.State);

        using var rest = new HttpClient { BaseAddress = baseUri };
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var watch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsync($"api/customers/me/after-sales/notifications/{aFirst}/read", null)).StatusCode);
        var read = await NextAsync(eventsA, e => e.EventId == aFirst);
        watch.Stop();
        Assert.Equal(RealtimeEventTypes.NotificationRead, read.EventType);
        Console.WriteLine($"REALTIME_LIVE first delivery {watch.Elapsed.TotalMilliseconds:F1} ms (REST round trip + delivery)");
        await Task.Delay(500);
        Assert.False(eventsB.Reader.TryRead(out _), "customer B received customer A's event");

        if (container is null) return;
        // Restart the API container: the client reconnects by itself; REST and delivery work again.
        var reconnected = new TaskCompletionSource();
        connectionA.Reconnected += _ => { reconnected.TrySetResult(); return Task.CompletedTask; };
        using (var restart = Process.Start(new ProcessStartInfo("docker", $"restart {container}") { RedirectStandardOutput = true, RedirectStandardError = true })!)
            await restart.WaitForExitAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(HubConnectionState.Connected, connectionA.State);
        Assert.Equal(HttpStatusCode.OK, (await rest.PostAsync($"api/customers/me/after-sales/notifications/{aSecond}/read", null)).StatusCode);
        Assert.Equal(RealtimeEventTypes.NotificationRead, (await NextAsync(eventsA, e => e.EventId == aSecond)).EventType);
        Console.WriteLine("REALTIME_LIVE reconnected after container restart and received the next event");
    }
}
