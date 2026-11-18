using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.Paging;

/// <summary>
/// PAGINATION (2026-11-18): opt-in SQL paging of lists that grow without bound. Boundaries (first / middle / last / beyond the last page),
/// parameter validation, a deterministic order when timestamps tie, the total of the filter, and no record of another user in counts or pages.
/// </summary>
public sealed class OptInPagingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData(null, null, true, 0, 0)]
    [InlineData(1, null, true, 1, PageRequest.DefaultPageSize)]
    [InlineData(null, 5, true, 1, 5)]
    [InlineData(3, 100, true, 3, 100)]
    [InlineData(10_000, 1, true, 10_000, 1)]
    [InlineData(0, 10, false, 0, 0)]
    [InlineData(-1, 10, false, 0, 0)]
    [InlineData(10_001, 10, false, 0, 0)]
    [InlineData(1, 0, false, 0, 0)]
    [InlineData(1, 101, false, 0, 0)]
    public void PAGINATION_Page_parameters_are_validated_and_absent_parameters_mean_the_legacy_list(int? page, int? pageSize, bool valid, int expectedPage, int expectedSize)
    {
        var parsed = PageRequest.Parse(page, pageSize);

        Assert.Equal(valid, parsed.Succeeded);
        if (!valid)
        {
            Assert.Equal(CommonErrorCodes.ValidationFailed, parsed.ErrorCode);
            return;
        }
        if (page is null && pageSize is null) Assert.Null(parsed.Value);
        else Assert.Equal((expectedPage, expectedSize), (parsed.Value!.Page, parsed.Value.PageSize));
    }

    [Fact]
    public async Task PAGINATION_Notifications_pages_cover_every_row_once_in_a_stable_order_without_other_users_rows()
    {
        await using var ctx = Create();
        var me = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Me");
        var other = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Other");
        var at = ctx.Clock.GetUtcNow();
        // 23 of mine (several share a timestamp, so the id must break ties), 7 of another user.
        for (var i = 0; i < 23; i++)
            ctx.Db.AfterSalesNotifications.Add(new AfterSalesNotification { Id = Guid.NewGuid(), RecipientUserId = me.Id, Type = "T", Message = $"m{i}", CreatedAt = at.AddMinutes(-(i / 3)) });
        for (var i = 0; i < 7; i++)
            ctx.Db.AfterSalesNotifications.Add(new AfterSalesNotification { Id = Guid.NewGuid(), RecipientUserId = other.Id, Type = "T", Message = $"o{i}", CreatedAt = at });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var customer = ctx.Customer();

        var first = await customer.PageNotificationsAsync(me.Id, new PageRequest(1, 10), None);
        var middle = await customer.PageNotificationsAsync(me.Id, new PageRequest(2, 10), None);
        var last = await customer.PageNotificationsAsync(me.Id, new PageRequest(3, 10), None);
        var beyond = await customer.PageNotificationsAsync(me.Id, new PageRequest(4, 10), None);

        Assert.Equal((23, 3, 10, 10, 3, 0), (first.TotalItems, first.TotalPages, first.Items.Count, middle.Items.Count, last.Items.Count, beyond.Items.Count));
        Assert.Equal(23, beyond.TotalItems);   // an empty page still reports the total
        var all = first.Items.Concat(middle.Items).Concat(last.Items).ToList();
        Assert.Equal(23, all.Select(n => n.NotificationId).Distinct().Count());   // no duplicate, no gap
        Assert.DoesNotContain(all, n => n.Message.StartsWith('o'));               // no other user's row
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.CreatedAt >= p.Second.CreatedAt));   // newest first

        // The order is deterministic: the same page twice is identical, ties included.
        var again = await customer.PageNotificationsAsync(me.Id, new PageRequest(2, 10), None);
        Assert.Equal(middle.Items.Select(n => n.NotificationId), again.Items.Select(n => n.NotificationId));

        // A user with nothing gets an empty first page with zero totals.
        var nobody = await customer.PageNotificationsAsync(Guid.NewGuid(), new PageRequest(1, 10), None);
        Assert.Equal((0, 0, 0), (nobody.TotalItems, nobody.TotalPages, nobody.Items.Count));
    }

    [Fact]
    public async Task PAGINATION_Paged_notifications_are_not_capped_at_100_like_the_legacy_list()
    {
        await using var ctx = Create();
        var me = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Me");
        var at = ctx.Clock.GetUtcNow();
        for (var i = 0; i < 130; i++)
            ctx.Db.AfterSalesNotifications.Add(new AfterSalesNotification { Id = Guid.NewGuid(), RecipientUserId = me.Id, Type = "T", Message = $"m{i}", CreatedAt = at.AddSeconds(-i) });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        Assert.Equal(100, (await ctx.Customer().NotificationsAsync(me.Id, None)).Count);
        var page = await ctx.Customer().PageNotificationsAsync(me.Id, new PageRequest(2, 100), None);
        Assert.Equal((130, 2, 30), (page.TotalItems, page.TotalPages, page.Items.Count));
        Assert.Equal("m129", page.Items[^1].Message);   // the oldest row is reachable
    }

    [Fact]
    public async Task PAGINATION_Request_pages_filter_by_status_in_SQL_and_customers_only_count_their_own()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        for (var i = 0; i < 3; i++) await ctx.FaultAsync(h, $"Fault {i}");

        var staff = await ctx.Manager().PageAsync(ServiceRequestStatuses.Submitted, null, new PageRequest(1, 2), None);
        Assert.Equal((3, 2, 2), (staff.TotalItems, staff.TotalPages, staff.Items.Count));
        Assert.Equal(0, (await ctx.Manager().PageAsync(ServiceRequestStatuses.Completed, null, new PageRequest(1, 2), None)).TotalItems);

        var mine = await ctx.Customer().PageAsync(h.CustomerId, new PageRequest(1, 10), None);
        Assert.Equal(3, mine.TotalItems);
        var stranger = await ctx.Customer().PageAsync(Guid.NewGuid(), new PageRequest(1, 10), None);
        Assert.Equal((0, 0), (stranger.TotalItems, stranger.Items.Count));
    }

    [Fact]
    public async Task PAGINATION_Sales_task_pages_follow_the_queue_order_and_each_sales_user_counts_only_their_own_tasks()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        for (var i = 0; i < 3; i++)
        {
            var fault = await ctx.FaultAsync(h, $"Fault {i}");
            var items = await ctx.InspectedAsync(h, fault.RequestId, Later(3 + i), new DiagnosedItemInput(null, $"Item {i}", "broken", null));
            await ctx.DecideAsync(h, fault.RequestId, (items[0], ItemCoverages.Chargeable));
        }
        var tasks = ctx.Tasks();
        var first = await tasks.PageOpenAsync(new PageRequest(1, 2), None);
        var second = await tasks.PageOpenAsync(new PageRequest(2, 2), None);
        Assert.Equal((3, 2, 2, 1), (first.TotalItems, first.TotalPages, first.Items.Count, second.Items.Count));
        Assert.Equal(3, first.Items.Concat(second.Items).Select(t => t.TaskId).Distinct().Count());

        Assert.True((await tasks.ClaimAsync(h.SalesId, first.Items[0].TaskId, None)).Succeeded);
        Assert.Equal(1, (await tasks.PageMineAsync(h.SalesId, new PageRequest(1, 10), None)).TotalItems);
        Assert.Equal(0, (await tasks.PageMineAsync(Guid.NewGuid(), new PageRequest(1, 10), None)).TotalItems);
        Assert.Equal(2, (await tasks.PageOpenAsync(new PageRequest(1, 10), None)).TotalItems);
        Assert.Equal(3, (await tasks.PageAllAsync(null, new PageRequest(1, 10), None)).TotalItems);
        Assert.Equal(1, (await tasks.PageAllAsync(SalesTaskStatuses.Claimed, new PageRequest(1, 10), None)).TotalItems);
    }

    [Fact]
    public async Task PAGINATION_Sales_survey_request_queues_page_in_SQL_with_totals_and_owner_isolation()
    {
        await using var ctx = SmartSolar.Tests.TestSupport.InstallationWorkflowTestContext.Create();
        var sales = await ctx.Scheduling.Inner.SeedUserAsync("Sales A");
        for (var i = 0; i < 5; i++) await ctx.Scheduling.SeedSurveyRequestAsync(SmartSolar.Tests.TestSupport.SchedulingTestContext.BinhTan, $"Customer {i}");
        var unitOfWork = new PreSurveyUnitOfWork(ctx.Db);
        var pending = new GetPendingSurveyRequestsHandler(unitOfWork, ctx.Clock);

        var p1 = await pending.HandleAsync(new PageRequest(1, 2), None);
        var p3 = await pending.HandleAsync(new PageRequest(3, 2), None);
        Assert.Equal((5, 3, 2, 1), (p1.TotalItems, p1.TotalPages, p1.Items.Count, p3.Items.Count));
        Assert.Equal((await pending.HandleAsync(None)).Count, p1.TotalItems);   // same rows as the legacy list

        Assert.True(await unitOfWork.TryClaimSurveyRequestAsync(p1.Items[0].SurveyRequestId, sales.Id, ctx.Clock.GetUtcNow(), None));
        var mine = await new GetMySurveyRequestsHandler(unitOfWork, ctx.Clock).HandleAsync(sales.Id, new PageRequest(1, 10), None);
        Assert.Equal((1, p1.Items[0].SurveyRequestId), (mine.TotalItems, mine.Items.Single().SurveyRequestId));
        Assert.Equal(0, (await new GetMySurveyRequestsHandler(unitOfWork, ctx.Clock).HandleAsync(Guid.NewGuid(), new PageRequest(1, 10), None)).TotalItems);
        Assert.Equal(4, (await pending.HandleAsync(new PageRequest(1, 10), None)).TotalItems);
    }
}
