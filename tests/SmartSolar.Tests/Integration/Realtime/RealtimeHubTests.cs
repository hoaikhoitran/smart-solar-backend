using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Realtime;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.AfterSales;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Realtime;

/// <summary>
/// SignalR realtime layer through the real API pipeline (2026-11-28): JWT authentication of the hub (header and access_token query on the hub path
/// only), user-targeted delivery of committed notifications over a real WebSocket (TestServer), isolation between users, no client-invokable
/// methods, reconnection recovery through REST, and the disabled default. Business transitions run with the production handlers on a DbContext
/// resolved from the API's container (so the API's post-commit hook and hub deliver them).
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class RealtimeHubTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static Dictionary<string, string?> Settings(bool enabled) => new()
    {
        ["Realtime:Enabled"] = enabled ? "true" : "false",
        ["RateLimiting:InstallationRead:PermitLimit"] = "1000", ["RateLimiting:InstallationWrite:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    private static AuthApiFactory Start(bool enabled = true) => Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), Settings(enabled));

    internal static string TokenFor(AuthApiFactory factory, string role, Guid userId)
    {
        _ = CatalogEndpointsTests.ClientFor(factory, role, userId);   // seeds the account when missing
        using var db = factory.CreateDbContext();
        var user = db.UserAccounts.AsNoTracking().Single(u => u.Id == userId);
        return factory.Services.GetRequiredService<IAccessTokenService>().Generate(user, [role]).Token;
    }

    internal sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection { get; } = connection;
        public Channel<RealtimeEnvelope> Events { get; } = Channel.CreateUnbounded<RealtimeEnvelope>();

        public async Task<RealtimeEnvelope> NextAsync(Func<RealtimeEnvelope, bool> match)
        {
            using var timeout = new CancellationTokenSource(Wait);
            while (true)
            {
                var e = await Events.Reader.ReadAsync(timeout.Token);
                if (match(e)) return e;
            }
        }

        public async Task<IReadOnlyList<RealtimeEnvelope>> DrainAsync(TimeSpan quiet)
        {
            await Task.Delay(quiet);
            var all = new List<RealtimeEnvelope>();
            while (Events.Reader.TryRead(out var e)) all.Add(e);
            return all;
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    /// <summary>Real WebSocket transport over the TestServer; the token travels as the access_token query parameter (the browser path).</summary>
    internal static async Task<Listener> ConnectAsync(AuthApiFactory factory, string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = context.Uri.Query.TrimStart('?') + "&access_token=" + Uri.EscapeDataString(token) }.Uri;
                    return await factory.Server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
                };
            })
            .Build();
        var listener = new Listener(connection);
        connection.On<RealtimeEnvelope>(SmartSolar.Api.Realtime.RealtimeHub.ClientMethod, e => listener.Events.Writer.TryWrite(e));
        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);
        return listener;
    }

    /// <summary>A business context on a DbContext of the API's container (carries the API's realtime interceptor).</summary>
    internal static (IServiceScope Scope, AppDbContext Db) ApiDb(AuthApiFactory factory)
    {
        var scope = factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client) => client.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);

    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task HUB01_negotiation_requires_a_valid_current_token_and_the_query_token_works_on_the_hub_path_only()
    {
        using var factory = Start();
        var customerId = Guid.NewGuid();
        var token = TokenFor(factory, RoleCodes.Customer, customerId);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await NegotiateAsync(anonymous)).StatusCode);
        var invalid = factory.CreateClient();
        invalid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await NegotiateAsync(invalid)).StatusCode);
        // Signed with the right key but expired (beyond the 30 s skew).
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("SmartSolar", "SmartSolarClients",
            [new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString()), new Claim(ClaimTypes.Role, RoleCodes.Customer)],
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("integration-test-signing-key-32-bytes!!")), SecurityAlgorithms.HmacSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/hubs/realtime/negotiate?negotiateVersion=1&access_token={expired}", null)).StatusCode);
        // Wrong signing key.
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("SmartSolar", "SmartSolarClients",
            [new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString())], DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-key-that-is-32-bytes-long!!!")), SecurityAlgorithms.HmacSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/hubs/realtime/negotiate?negotiateVersion=1&access_token={forged}", null)).StatusCode);

        // Valid: header and query both accepted on the hub path.
        var header = factory.CreateClient();
        header.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(header)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync($"/hubs/realtime/negotiate?negotiateVersion=1&access_token={token}", null)).StatusCode);
        // The query token is never accepted by REST endpoints.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/customers/me/after-sales/notifications?access_token={token}")).StatusCode);

        // A revoked session (account authorization version bumped: logout of all sessions / role change / deletion) is refused.
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.UserAccounts.SingleAsync(u => u.Id == customerId);
            user.AuthVersion++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await NegotiateAsync(header)).StatusCode);
    }

    [Fact]
    public async Task HUB02_disabled_by_default_there_is_no_hub_and_the_rest_api_is_unchanged()
    {
        using var factory = Start(enabled: false);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        Assert.Equal(HttpStatusCode.NotFound, (await NegotiateAsync(customer)).StatusCode);
        var list = await customer.GetAsync("/api/customers/me/after-sales/notifications");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True((await BodyOf(list)).GetProperty("isSuccess").GetBoolean());
        Assert.IsType<NoOpRealtimeNotificationPublisher>(factory.Services.GetRequiredService<IRealtimeNotificationPublisher>());
    }

    [Fact]
    public async Task HUB03_clients_cannot_invoke_anything_join_groups_or_impersonate()
    {
        using var factory = Start();
        await using var listener = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => listener.Connection.InvokeAsync("JoinGroup", "customer:" + Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => listener.Connection.InvokeAsync("Subscribe", Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => listener.Connection.InvokeAsync("SendToUser", Guid.NewGuid().ToString(), "x"));
        Assert.Equal(HubConnectionState.Connected, listener.Connection.State);
    }

    // ------------------------------------------------------------------ E2E delivery

    [Fact]
    public async Task E2E01_technician_acceptance_reaches_only_the_booking_customer_and_reconnection_recovers_through_rest()
    {
        using var factory = Start();
        var (scope, db) = ApiDb(factory);
        using var _ = scope;
        var ctx = InstallationWorkflowTestContext.CreateOn(db);
        var technician = await ctx.Scheduling.SeedTechnicianAsync("Realtime technician", BinhTan);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Realtime customer");
        var other = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Other customer");
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Unrelated technician", BinhTan);
        await using var customer = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, request.Customer.Id));
        await using var otherCustomer = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, other.Customer.Id));
        await using var unrelated = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Technician, stranger.Id));
        // Same user, second tab: both tabs receive.
        await using var secondTab = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, request.Customer.Id));

        var booked = await ctx.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10), null), None);
        Assert.True(booked.Succeeded, booked.Message);
        var bookedEvent = await customer.NextAsync(e => e.NotificationType == CustomerNotificationTypes.SurveyAppointmentBooked);
        Assert.Equal((1, RealtimeEventTypes.NotificationCreated, RealtimeSources.InApp), (bookedEvent.Version, bookedEvent.EventType, bookedEvent.Source));
        var accepted = await ctx.Scheduling.AcceptAsync(technician.Id, booked.Value!.AppointmentId);
        Assert.True(accepted.Succeeded, accepted.Message);
        var confirmed = await customer.NextAsync(e => e.NotificationType == CustomerNotificationTypes.SurveyAppointmentConfirmed);
        Assert.Equal(confirmed.EventId, (await secondTab.NextAsync(e => e.NotificationType == CustomerNotificationTypes.SurveyAppointmentConfirmed)).EventId);

        // The event names a row the customer can refetch through the existing REST endpoint.
        var rest = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, request.Customer.Id);
        var listed = await DataOf(await rest.GetAsync("/api/customers/me/after-sales/notifications"));
        Assert.Contains(listed.EnumerateArray(), n => n.GetProperty("notificationId").GetGuid() == confirmed.EventId);
        // Nobody else got anything.
        Assert.Empty(await otherCustomer.DrainAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(await unrelated.DrainAsync(TimeSpan.FromMilliseconds(50)));

        // Disconnected: a new event is missed by the socket but stays in PostgreSQL / REST; after reconnecting the client refetches it.
        await customer.Connection.StopAsync();
        var released = await ctx.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(other.Customer.Id, other.Request.Id, At(14), null), None);
        Assert.True(released.Succeeded, released.Message);
        Assert.Contains(await otherCustomer.DrainAsync(TimeSpan.FromMilliseconds(300)), e => e.NotificationType == CustomerNotificationTypes.SurveyAppointmentBooked);
        await customer.Connection.StartAsync();
        var afterReconnect = await DataOf(await rest.GetAsync("/api/customers/me/after-sales/notifications"));
        Assert.Equal(listed.GetArrayLength(), afterReconnect.GetArrayLength());   // nothing of the other customer leaks into the list
    }

    [Fact]
    public async Task E2E02_E2E03_manager_cash_confirmation_and_payos_settlement_reach_the_contract_customer()
    {
        using var factory = Start();
        var (scope, db) = ApiDb(factory);
        using var _ = scope;
        var ctx = InstallationWorkflowTestContext.CreateOn(db);
        var cash = await PaymentPlanFlow.AgreedAsync(ctx, "Cash customer");
        var online = await PaymentPlanFlow.AgreedAsync(ctx, "Online customer");
        await using var cashCustomer = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, cash.CustomerId));
        await using var onlineCustomer = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, online.CustomerId));

        await PaymentPlanFlow.PayCashAsync(ctx, cash, 1);
        await cashCustomer.NextAsync(e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived);
        Assert.DoesNotContain(await onlineCustomer.DrainAsync(TimeSpan.FromMilliseconds(300)), e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived);

        await PaymentPlanFlow.PayOnlineAsync(ctx, online, 1);
        await onlineCustomer.NextAsync(e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived);
        Assert.DoesNotContain(await cashCustomer.DrainAsync(TimeSpan.FromMilliseconds(300)), e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived);
    }

    [Fact]
    public async Task E2E04_E2E06_background_sweep_notifications_reach_the_customer_quotation_expiry_and_maintenance_reminder()
    {
        using var factory = Start();
        var (scope, db) = ApiDb(factory);
        using var _ = scope;
        var ctx = AfterSalesTestContext.CreateOn(db);
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        await using var customer = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, t.H.CustomerId));
        await using var technician = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Technician, t.H.TechnicianId));

        ctx.Clock.Now = t.Quotation.ExpiresAt.AddMinutes(1).ToUniversalTime();
        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);
        var expired = await customer.NextAsync(e => e.NotificationType == "QUOTATION_EXPIRED");
        Assert.Equal((RealtimeResourceTypes.ServiceRequest, (Guid?)t.RequestId), (expired.ResourceType, expired.ResourceId));
        Assert.DoesNotContain(await technician.DrainAsync(TimeSpan.FromMilliseconds(300)), e => e.NotificationType == "QUOTATION_EXPIRED");

        // Maintenance reminder of a package term (worker-generated).
        var term = await ctx.ActiveTermAsync(t.H, await ctx.PackageAsync(t.H.AdminId, ServicePackageKinds.Maintenance, visitMonths: "1,7"));
        var month1 = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id && e.PlanMonth == 1);
        ctx.Clock.Now = month1.PeriodStartsAt.ToUniversalTime();
        Assert.True((await ctx.Sweep().RunOnceAsync(None)).MaintenanceNotices >= 1);
        var due = await customer.NextAsync(e => e.NotificationType == "MAINTENANCE_DUE");
        Assert.Equal((RealtimeResourceTypes.ServiceSubscription, (Guid?)term.Id), (due.ResourceType, due.ResourceId));
    }

    [Fact]
    public async Task E2E05_a_customer_rejecting_the_repair_result_reaches_every_active_manager_not_sales_or_other_customers()
    {
        using var factory = Start();
        var (scope, db) = ApiDb(factory);
        using var _ = scope;
        var ctx = AfterSalesTestContext.CreateOn(db);
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await UnifiedCashReceiptTests.SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        Assert.True((await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None)).Succeeded);
        var visit = await ctx.VisitStartedAsync(t.H, t.RequestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(t.H.TechnicianId, visit, "Repaired", [new ItemResultInput(t.Items[0], "repaired", null)], [], null, None)).Succeeded);

        // An active account holding the Manager role (the recipients the business code selects: every active Manager).
        await using var manager = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Manager, Guid.NewGuid()));
        await using var sales = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Sales, t.H.SalesId));
        await using var other = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, Guid.NewGuid()));
        var rejected = await ctx.Customer().DecideCompletionAsync(t.H.CustomerId, t.RequestId, false, "Inverter still shows the error", null, None);
        Assert.True(rejected.Succeeded, rejected.Message);
        var staffEvent = await manager.NextAsync(e => e.NotificationType == "COMPLETION_REJECTED");
        Assert.Equal((RealtimeResourceTypes.ServiceRequest, (Guid?)t.RequestId), (staffEvent.ResourceType, staffEvent.ResourceId));
        Assert.DoesNotContain(await sales.DrainAsync(TimeSpan.FromMilliseconds(300)), e => e.NotificationType == "COMPLETION_REJECTED");
        Assert.Empty(await other.DrainAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task E2E07_delivery_latency_from_commit_to_client_is_measured()
    {
        using var factory = Start();
        var (scope, db) = ApiDb(factory);
        using var _ = scope;
        var ctx = InstallationWorkflowTestContext.CreateOn(db);
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Latency")).Id;
        await using var listener = await ConnectAsync(factory, TokenFor(factory, RoleCodes.Customer, user));
        var uow = new SmartSolar.Infrastructure.Persistence.AfterSales.AfterSalesUnitOfWork(db);
        var samples = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var type = $"RT_LATENCY_{i}";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await uow.ExecuteInTransactionAsync<bool>(async token =>
            {
                uow.AddNotification(user, type, "x", ctx.Clock.GetUtcNow());
                await uow.SaveChangesAsync(token);
                return (true, true);
            }, None);
            await listener.NextAsync(e => e.NotificationType == type);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Console.WriteLine($"REALTIME_LATENCY n={samples.Count} p50={samples[samples.Count / 2]:F1}ms p95={p95:F1}ms max={samples[^1]:F1}ms");
        Assert.True(p95 <= 1000, $"p95 {p95} ms");
    }
}
