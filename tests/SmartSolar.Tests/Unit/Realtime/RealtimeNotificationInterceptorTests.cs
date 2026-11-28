using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Extensions;
using SmartSolar.Infrastructure.Persistence.AfterSales;
using SmartSolar.Infrastructure.Persistence.Realtime;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Realtime;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.AfterSales;

namespace SmartSolar.Tests.Unit.Realtime;

/// <summary>
/// Post-commit realtime hook (2026-11-28) on SQLite with the production handlers: committed notification rows ↔ published events (exact recipients,
/// after the commit only), nothing for failed / rolled-back work, failure and timeout isolation, safe payload, feature flag.
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class RealtimeNotificationInterceptorTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(HashSet<Guid> InApp, HashSet<Guid> Scheduling, HashSet<Guid> Dispatch)> RowsAsync(Microsoft.EntityFrameworkCore.DbContext db)
    {
        var app = (SmartSolar.Infrastructure.Persistence.AppDbContext)db;
        return ((await app.AfterSalesNotifications.AsNoTracking().Select(n => n.Id).ToListAsync()).ToHashSet(),
            (await app.SchedulingNotifications.AsNoTracking().Select(n => n.Id).ToListAsync()).ToHashSet(),
            (await app.Set<SmartSolar.Modules.Scheduling.Dispatch.DispatchNotification>().AsNoTracking().Select(n => n.Id).ToListAsync()).ToHashSet());
    }

    // ------------------------------------------------------------------ feature flag

    [Fact]
    public void RT01_disabled_by_default_registers_only_the_no_op_publisher_and_no_interceptor()
    {
        var services = new ServiceCollection();
        services.AddRealtime(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<RealtimeOptions>().Enabled);
        Assert.IsType<NoOpRealtimeNotificationPublisher>(provider.GetRequiredService<IRealtimeNotificationPublisher>());
        Assert.Null(provider.GetService<RealtimeNotificationInterceptor>());
        Assert.True(new NoOpRealtimeNotificationPublisher().PublishAsync([], None).IsCompletedSuccessfully);

        var enabled = new ServiceCollection();
        enabled.AddLogging();
        enabled.AddRealtime(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Enabled"] = "true" }).Build());
        Assert.Contains(enabled, d => d.ServiceType == typeof(RealtimeNotificationInterceptor));
        Assert.Contains(enabled, d => d.ServiceType == typeof(IRealtimeNotificationPublisher) && d.ImplementationType == typeof(SmartSolar.Api.Realtime.SignalRRealtimePublisher));
        // Out-of-range settings are refused at startup.
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRealtime(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Enabled"] = "true", ["Realtime:PublishTimeoutMilliseconds"] = "5" }).Build()));
    }

    // ------------------------------------------------------------------ business flows (happy cases)

    [Fact]
    public async Task RT02_booking_and_technician_acceptance_publish_exactly_the_committed_rows_to_their_recipients()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var survey = await ctx.ConfirmedSurveyAsync("Realtime customer");

        var (inApp, scheduling, _) = await RowsAsync(ctx.Db);
        var events = recorder.Snapshot();
        Assert.NotEmpty(inApp);
        Assert.Equal(inApp, events.Where(e => e.Source == RealtimeSources.InApp).Select(e => e.NotificationId).ToHashSet());
        Assert.Equal(scheduling, events.Where(e => e.Source == RealtimeSources.Scheduling).Select(e => e.NotificationId).ToHashSet());
        Assert.Equal(events.Count, events.Select(e => e.NotificationId).Distinct().Count());   // once each
        // The customer's in-app events are addressed to that customer only, with the persisted recipient.
        var customerId = survey.Request.Customer.Id;
        var confirmed = Assert.Single(events, e => e.NotificationType == CustomerNotificationTypes.SurveyAppointmentConfirmed);
        Assert.Equal((customerId, RealtimeEventTypes.NotificationCreated), (confirmed.RecipientUserId, confirmed.EventType));
        foreach (var e in events.Where(e => e.Source == RealtimeSources.InApp))
            Assert.Equal((await ctx.Db.AfterSalesNotifications.AsNoTracking().SingleAsync(n => n.Id == e.NotificationId)).RecipientUserId, e.RecipientUserId);
        foreach (var e in events.Where(e => e.Source == RealtimeSources.Scheduling))
        {
            var row = await ctx.Db.SchedulingNotifications.AsNoTracking().SingleAsync(n => n.Id == e.NotificationId);
            Assert.Equal((row.RecipientUserId, RealtimeResourceTypes.Appointment, (Guid?)row.AppointmentId), (e.RecipientUserId, e.ResourceType, e.ResourceId));
        }
    }

    [Fact]
    public async Task RT03_a_manager_cash_confirmation_and_a_payos_webhook_notify_the_contract_customer_and_a_duplicate_webhook_publishes_nothing()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var cashFlow = await PaymentPlanFlow.AgreedAsync(ctx, "Cash customer");
        recorder.Clear();
        await PaymentPlanFlow.PayCashAsync(ctx, cashFlow, 1);
        var deposit = Assert.Single(recorder.Snapshot(), e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived);
        Assert.Equal(cashFlow.CustomerId, deposit.RecipientUserId);
        Assert.All(recorder.Snapshot(), e => Assert.Equal(cashFlow.CustomerId, e.RecipientUserId));

        var online = await PaymentPlanFlow.AgreedAsync(ctx, "Online customer");
        var opened = await ctx.InstallmentPayments().StartAsync(Modules.CustomerContracts.InstallmentPayer.Technician, online.TechnicianId, online.ContractId, 1, "ONLINE", None);
        var qr = opened.Value!.Current!;
        recorder.Clear();
        var webhook = ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount, "FT-RT03", transactionAt: ctx.Clock.GetUtcNow());
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.PaymentsWithSchedule().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(online.CustomerId, Assert.Single(recorder.Snapshot(), e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived).RecipientUserId);
        Assert.DoesNotContain(recorder.Snapshot(), e => e.RecipientUserId == cashFlow.CustomerId);   // the other customer never gets it
        recorder.Clear();
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.PaymentsWithSchedule().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public async Task RT04_the_after_sales_sweep_worker_publishes_its_committed_customer_notification()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = AfterSalesTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        ctx.Clock.Now = t.Quotation.ExpiresAt.AddMinutes(1).ToUniversalTime();
        recorder.Clear();
        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);
        var expired = Assert.Single(recorder.Snapshot(), e => e.NotificationType == "QUOTATION_EXPIRED");
        Assert.Equal((t.H.CustomerId, RealtimeResourceTypes.ServiceRequest, (Guid?)t.RequestId), (expired.RecipientUserId, expired.ResourceType, expired.ResourceId));
        // Run again: nothing more to do → nothing published.
        recorder.Clear();
        await ctx.Sweep().RunOnceAsync(None);
        Assert.DoesNotContain(recorder.Snapshot(), e => e.NotificationType == "QUOTATION_EXPIRED");
    }

    [Fact]
    public async Task RT05_marking_a_notification_read_publishes_a_read_event_to_its_owner_only_once()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = AfterSalesTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var h = await ctx.HandedOverAsync();
        var notification = await ctx.Db.AfterSalesNotifications.AsNoTracking().FirstAsync(n => n.RecipientUserId == h.CustomerId && n.ReadAt == null);
        recorder.Clear();
        Assert.True((await ctx.Customer().MarkNotificationReadAsync(h.CustomerId, notification.Id, None)).Succeeded);
        var read = Assert.Single(recorder.Snapshot());
        Assert.Equal((RealtimeEventTypes.NotificationRead, h.CustomerId, notification.Id), (read.EventType, read.RecipientUserId, read.NotificationId));
        recorder.Clear();
        Assert.True((await ctx.Customer().MarkNotificationReadAsync(h.CustomerId, notification.Id, None)).Succeeded);   // idempotent
        Assert.Empty(recorder.Snapshot());
        // Another user cannot mark it (404) and nothing is published.
        Assert.False((await ctx.Customer().MarkNotificationReadAsync(h.TechnicianId, notification.Id, None)).Succeeded);
        Assert.Empty(recorder.Snapshot());
    }

    // ------------------------------------------------------------------ nothing for refused / failed / rolled-back work

    [Fact]
    public async Task RT06_refused_business_operations_publish_nothing()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = AfterSalesTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        recorder.Clear();
        var callsBefore = recorder.Calls;
        Assert.False((await ctx.Quotations().RecordOnSiteDecisionAsync(stranger.Id, t.Quotation.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        Assert.False((await ctx.Quotations().RecordOnSiteDecisionAsync(t.H.TechnicianId, t.Quotation.QuotationId, "MAYBE", null, None)).Succeeded);
        Assert.False((await ctx.Customer().CancelAsync(t.H.CustomerId, t.RequestId, null, None)).Succeeded);
        Assert.Empty(recorder.Snapshot());
        Assert.Equal(callsBefore, recorder.Calls);   // the publisher was not even called
    }

    [Fact]
    public async Task RT07_rolled_back_or_failed_transactions_publish_nothing_and_a_commit_publishes_only_after_it_committed()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Recipient")).Id;
        var uow = new AfterSalesUnitOfWork(ctx.Db);

        // Rolled back.
        await uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(user, "RT_ROLLED_BACK", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            return (true, false);
        }, None);
        // Thrown after SaveChanges (the transaction is disposed without a commit).
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(user, "RT_THROWN", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            throw new InvalidOperationException("business failure after the insert");
        }, None));
        Assert.Empty(recorder.Snapshot());
        Assert.Equal(0, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.RecipientUserId == user));

        // Committed: nothing is visible to the publisher while the transaction is open; afterwards exactly the committed row (no stale pending).
        var countWhileOpen = -1;
        await uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(user, "RT_COMMITTED", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            countWhileOpen = recorder.Published.Count;
            return (true, true);
        }, None);
        Assert.Equal(0, countWhileOpen);
        var published = Assert.Single(recorder.Snapshot());
        Assert.Equal(("RT_COMMITTED", user), (published.NotificationType, published.RecipientUserId));
        Assert.Equal(published.NotificationId, (await ctx.Db.AfterSalesNotifications.AsNoTracking().SingleAsync(n => n.RecipientUserId == user)).Id);
    }

    [Fact]
    public async Task RT08_a_failed_save_publishes_nothing()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder));
        var uow = new AfterSalesUnitOfWork(ctx.Db);
        uow.AddNotification(Guid.NewGuid(), "RT_FK_FAILURE", "x", ctx.Clock.GetUtcNow());   // unknown recipient: the FK refuses the insert
        await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveChangesAsync(None));
        Assert.Empty(recorder.Snapshot());
    }

    // ------------------------------------------------------------------ failure isolation

    [Fact]
    public async Task RT09_a_failing_or_hanging_publisher_never_fails_delays_or_rolls_back_the_committed_operation()
    {
        var recorder = new RecordingRealtimePublisher { Throw = true };
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder, new RealtimeOptions { Enabled = true, PublishTimeoutMilliseconds = 200 }));
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);   // every step publishes and every publication throws
        Assert.True(recorder.Calls > 0);
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        Assert.Equal(Modules.CustomerContracts.InstallmentStatuses.Paid, (await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        Assert.True(await ctx.Db.AfterSalesNotifications.AnyAsync(n => n.Type == CustomerNotificationTypes.ContractDepositReceived));

        recorder.Throw = false;
        recorder.Hang = TimeSpan.FromSeconds(10);   // ignores cancellation
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Hang")).Id;
        var uow = new AfterSalesUnitOfWork(ctx.Db);
        var watch = Stopwatch.StartNew();
        await uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(user, "RT_HANG", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            return (true, true);
        }, None);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");   // bounded by the 200 ms timeout
        Assert.Equal(1, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.RecipientUserId == user));
    }

    [Fact]
    public async Task RT10_publication_is_bounded_per_commit()
    {
        var recorder = new RecordingRealtimePublisher();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Sqlite(recorder, new RealtimeOptions { Enabled = true, MaxEventsPerCommit = 3 }));
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Many")).Id;
        var uow = new AfterSalesUnitOfWork(ctx.Db);
        for (var i = 0; i < 10; i++) uow.AddNotification(user, $"RT_BULK_{i}", "x", ctx.Clock.GetUtcNow());
        await uow.SaveChangesAsync(None);   // no explicit transaction: EF's implicit one has committed when SavedChanges runs
        Assert.Equal(3, recorder.Snapshot().Count);
        Assert.Equal(10, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.RecipientUserId == user));
    }

    // ------------------------------------------------------------------ payload

    [Fact]
    public void RT11_the_envelope_carries_ids_and_codes_only()
    {
        var n = new RealtimeNotification(Guid.NewGuid(), Guid.NewGuid(), RealtimeEventTypes.NotificationCreated, RealtimeSources.InApp, "CONTRACT_DEPOSIT_RECEIVED",
            RealtimeResourceTypes.Notification, Guid.NewGuid(), DateTimeOffset.Parse("2026-11-28T10:00:00+07:00"));
        var json = JsonSerializer.Serialize(RealtimeEnvelope.From(n), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "eventId", "eventType", "notificationType", "occurredAt", "resourceId", "resourceType", "source", "version" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(n.NotificationId, doc.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(TimeSpan.Zero, doc.RootElement.GetProperty("occurredAt").GetDateTimeOffset().Offset);   // UTC
        Assert.DoesNotContain("recipient", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
    }
}
