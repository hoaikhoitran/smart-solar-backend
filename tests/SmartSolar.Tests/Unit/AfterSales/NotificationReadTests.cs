using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>Persistent read state of in-app notifications: owner only, idempotent, and the unread count follows the stored read_at.</summary>
public sealed class NotificationReadTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Marking_a_notification_read_persists_read_at_once_and_only_for_its_owner()
    {
        await using var ctx = Create();
        var customer = (await ctx.Installation.Scheduling.Inner.SeedUserAsync("Reader")).Id;
        var other = (await ctx.Installation.Scheduling.Inner.SeedUserAsync("Other")).Id;
        var now = ctx.Clock.GetUtcNow();
        ctx.Db.AfterSalesNotifications.AddRange(
            AfterSalesHistory.NewNotification(customer, "INSTALLATION_SCHEDULED", "Scheduled", now),
            AfterSalesHistory.NewNotification(customer, "CONTRACT_SIGNED", "Signed", now.AddMinutes(-1)),
            AfterSalesHistory.NewNotification(other, "CONTRACT_SIGNED", "Signed", now));
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var mine = await ctx.Customer().NotificationsAsync(customer, None);
        Assert.Equal(2, mine.Count(n => n.ReadAt is null));

        var first = mine[0];
        var read = await ctx.Customer().MarkNotificationReadAsync(customer, first.NotificationId, None);
        Assert.True(read.Succeeded, read.Message);
        Assert.Equal(now, read.Value!.ReadAt);

        ctx.Clock.Now = now.AddMinutes(5);
        var again = await ctx.Customer().MarkNotificationReadAsync(customer, first.NotificationId, None);
        Assert.True(again.Succeeded);
        Assert.Equal(now, again.Value!.ReadAt);                                                   // idempotent: the first read time stays
        Assert.Equal(now, (await ctx.Db.AfterSalesNotifications.AsNoTracking().SingleAsync(n => n.Id == first.NotificationId)).ReadAt);

        // Another user's notification is not found (not forbidden), and nothing changes.
        var stranger = await ctx.Customer().MarkNotificationReadAsync(other, first.NotificationId, None);
        Assert.Equal(AfterSalesErrorCodes.NotificationNotFound, stranger.ErrorCode);
        Assert.Equal(404, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(stranger.ErrorCode!));
        Assert.Equal(AfterSalesErrorCodes.NotificationNotFound, (await ctx.Customer().MarkNotificationReadAsync(customer, Guid.NewGuid(), None)).ErrorCode);

        var after = await ctx.Customer().NotificationsAsync(customer, None);
        Assert.Equal(1, after.Count(n => n.ReadAt is null));                                      // derived from the persisted read_at
        Assert.Null((await ctx.Customer().NotificationsAsync(other, None)).Single().ReadAt);
    }
}
