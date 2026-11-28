using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Realtime;

/// <summary>Post-commit realtime hook on a disposable PostgreSQL database (2026-11-28): real transactions, real visibility, the production retry strategy.</summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresRealtimeCommitTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Every_published_event_is_already_visible_to_another_connection_and_rolled_back_work_is_never_published()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var recorder = new RecordingRealtimePublisher();
        var visibleAtPublish = new List<bool>();
        recorder.OnPublish = batch =>
        {
            // A separate connection sees only committed rows: every published notification must already be there.
            using var other = database.NewContext();
            var ids = batch.Select(n => n.NotificationId).ToList();
            visibleAtPublish.Add(other.AfterSalesNotifications.Count(n => ids.Contains(n.Id))
                                 + other.SchedulingNotifications.Count(n => ids.Contains(n.Id))
                                 + other.Set<SmartSolar.Modules.Scheduling.Dispatch.DispatchNotification>().Count(n => ids.Contains(n.Id)) == ids.Count);
        };
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Postgres(database, RealtimeTestSupport.Interceptor(recorder)));

        // Real business journey: agreement then a Manager cash confirmation (explicit transactions with the retrying strategy).
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        Assert.NotEmpty(visibleAtPublish);
        Assert.All(visibleAtPublish, Assert.True);
        Assert.Contains(recorder.Snapshot(), e => e.NotificationType == CustomerNotificationTypes.ContractDepositReceived && e.RecipientUserId == flow.CustomerId);
        Assert.Equal(await ctx.Db.AfterSalesNotifications.CountAsync(), recorder.Snapshot().Count(e => e.Source == Modules.Common.Realtime.RealtimeSources.InApp));

        // Rolled back and thrown work on PostgreSQL: nothing published, nothing stored.
        recorder.Clear();
        var uow = new AfterSalesUnitOfWork(ctx.Db);
        await uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(flow.CustomerId, "RT_PG_ROLLBACK", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            return (true, false);
        }, None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteInTransactionAsync<bool>(async token =>
        {
            uow.AddNotification(flow.CustomerId, "RT_PG_THROWN", "x", ctx.Clock.GetUtcNow());
            await uow.SaveChangesAsync(token);
            throw new InvalidOperationException("failure after the insert");
        }, None));
        Assert.Empty(recorder.Snapshot());
        Assert.Equal(0, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.Type.StartsWith("RT_PG_")));
    }

    [PostgresFact]
    public async Task A_failing_publisher_never_rolls_back_or_fails_a_committed_payment_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var recorder = new RecordingRealtimePublisher { Throw = true };
        await using var ctx = InstallationWorkflowTestContext.CreateOn(RealtimeTestSupport.Postgres(database, RealtimeTestSupport.Interceptor(recorder)));
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        await using var check = database.NewContext();
        Assert.Equal(Modules.CustomerContracts.InstallmentStatuses.Paid,
            (await check.ContractInstallments.AsNoTracking().SingleAsync(i => i.ContractId == flow.ContractId && i.Sequence == 1)).Status);
        Assert.True(await check.AfterSalesNotifications.AnyAsync(n => n.Type == CustomerNotificationTypes.ContractDepositReceived));
        Assert.True(recorder.Calls > 0);
    }
}
