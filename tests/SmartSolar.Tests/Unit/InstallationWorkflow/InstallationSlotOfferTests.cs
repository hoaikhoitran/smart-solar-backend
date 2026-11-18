using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// INSTALL-SCHEDULE customer choice (2026-11-18): the technician consults the backend's availability and offers intervals; the customer sees
/// them on the portal (re-checked live) and selects one; the backend re-validates under the locks and records the normal PLANNED schedule, which
/// the payment confirms. Offers hold no capacity, stale or taken offers are refused, history is kept.
/// </summary>
public sealed class InstallationSlotOfferTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SlotInterval First = new(At(8, 0, Day.AddDays(1)), At(12, 0, Day.AddDays(2)));
    private static readonly SlotInterval Second = new(At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)));

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_001_Technician_consults_availability_offers_two_times_customer_selects_one_and_payment_confirms_it()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var contractId = signed.Contract.ContractId;
        var technician = signed.Cart.TechnicianId;
        var customer = signed.Cart.CustomerId;

        // Availability: working start days only, each with the interval it would occupy, checked like a plan.
        var availability = await schedules.AvailabilityAsync(technician, contractId, Day.AddDays(1), Day.AddDays(7), 2, None);
        Assert.True(availability.Succeeded, availability.Message);
        Assert.NotEmpty(availability.Value!);
        var tomorrow = Assert.Single(availability.Value!, a => a.Date == Day.AddDays(1));
        Assert.True(tomorrow.Available, tomorrow.Reason);
        Assert.True(tomorrow.EndsAt > tomorrow.StartsAt);

        var offered = await schedules.PublishOffersAsync(technician, contractId, [First, Second], "Chọn giúp em một ngày ạ", None);
        Assert.True(offered.Succeeded, offered.Message);
        Assert.Equal(2, offered.Value!.Count);
        Assert.True(await ctx.Db.AfterSalesNotifications.AnyAsync(n => n.RecipientUserId == customer && n.Type == CustomerNotificationTypes.InstallationSlotsOffered));
        // An offer is not a plan and holds nothing.
        Assert.Equal(0, await ctx.Db.InstallationScheduleProposals.CountAsync());

        var visible = (await schedules.ListOffersForCustomerAsync(customer, contractId, None)).Value!;
        Assert.Equal(2, visible.Count);
        Assert.All(visible, o => Assert.True(o.Available, o.UnavailableReason));
        Assert.All(visible, o => Assert.Null(o.WithdrawnReason));

        var chosen = visible.Single(o => o.StartsAt == First.StartsAt.ToUniversalTime());
        var selected = await schedules.SelectOfferAsync(customer, chosen.OfferId, None);
        Assert.True(selected.Succeeded, selected.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, false, true, technician), (selected.Value!.Status, selected.Value.IsConfirmed, selected.Value.AwaitingPayment,
            selected.Value.TechnicianUserId));
        Assert.Equal((First.StartsAt.ToUniversalTime(), First.EndsAt.ToUniversalTime()), (selected.Value.StartsAt.ToUniversalTime(), selected.Value.EndsAt.ToUniversalTime()));
        Assert.Contains("Khách hàng tự chọn", selected.Value.CustomerAgreementNote);
        var stored = await ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync();
        Assert.Equal(customer, stored.CreatedByUserId);

        var offers = await ctx.Db.InstallationSlotOffers.AsNoTracking().ToListAsync();
        var picked = offers.Single(o => o.Id == chosen.OfferId);
        Assert.Equal((InstallationSlotOfferStatuses.Selected, (Guid?)selected.Value.ScheduleId), (picked.Status, picked.SelectedScheduleId));
        Assert.Equal((InstallationSlotOfferStatuses.Withdrawn, "ANOTHER_OFFER_SELECTED"), (offers.Single(o => o.Id != chosen.OfferId).Status,
            offers.Single(o => o.Id != chosen.OfferId).WithdrawnReason));
        Assert.Empty((await schedules.ListOffersForCustomerAsync(customer, contractId, None)).Value!);

        // A double click returns the same schedule; nothing is written twice.
        var again = await schedules.SelectOfferAsync(customer, chosen.OfferId, None);
        Assert.Equal(selected.Value.ScheduleId, again.Value!.ScheduleId);
        Assert.Equal(1, await ctx.Db.InstallationScheduleProposals.CountAsync());

        // Payment confirms the customer's choice exactly like a technician-recorded plan.
        await InstallationScheduleTests.PayCashAsync(ctx, signed);
        var confirmed = (await schedules.GetForCustomerAsync(customer, contractId, None)).Value!.First();
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (confirmed.Status, confirmed.IsConfirmed));
        Assert.Equal(1, await ctx.Db.InstallationAssignments.CountAsync());
    }

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_002_Offers_and_availability_requests_are_validated_and_impossible_times_are_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var contractId = signed.Contract.ContractId;
        var technician = signed.Cart.TechnicianId;

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.PublishOffersAsync(technician, contractId, [], null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.PublishOffersAsync(technician, contractId, [First, First], null, None)).ErrorCode);
        var six = Enumerable.Range(1, 6).Select(i => new SlotInterval(At(8, 0, Day.AddDays(i)), At(10, 0, Day.AddDays(i)))).ToList();
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.PublishOffersAsync(technician, contractId, six, null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.PublishOffersAsync(technician, contractId, [First], new string('x', 501), None)).ErrorCode);
        // In the past, or ending before it starts.
        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable,
            (await schedules.PublishOffersAsync(technician, contractId, [new SlotInterval(At(8, 0, Day.AddDays(-2)), At(12, 0, Day.AddDays(-2)))], null, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable,
            (await schedules.PublishOffersAsync(technician, contractId, [new SlotInterval(First.EndsAt, First.StartsAt)], null, None)).ErrorCode);

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.AvailabilityAsync(technician, contractId, Day.AddDays(1), Day.AddDays(40), 1, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.AvailabilityAsync(technician, contractId, Day.AddDays(5), Day.AddDays(1), 1, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await schedules.AvailabilityAsync(technician, contractId, Day.AddDays(1), Day.AddDays(2), 0, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationSlotOffers.CountAsync());

        // Before the contract is SIGNED nothing can be offered.
        await using var other = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(other);
        var draft = await SimplifiedFlowFixture.AcceptAsync(other, cart);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotSigned,
            (await other.Schedules().PublishOffersAsync(cart.TechnicianId, draft.ContractId, [First], null, None)).ErrorCode);
    }

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_003_Another_technician_or_customer_gets_404()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var contractId = signed.Contract.ContractId;
        var offer = (await schedules.PublishOffersAsync(signed.Cart.TechnicianId, contractId, [First], null, None)).Value!.Single();
        var stranger = await ctx.Scheduling.Inner.SeedUserAsync("Stranger");

        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.PublishOffersAsync(stranger.Id, contractId, [Second], null, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.AvailabilityAsync(stranger.Id, contractId, Day.AddDays(1), Day.AddDays(2), 1, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.ListOffersForTechnicianAsync(stranger.Id, contractId, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.ListOffersForCustomerAsync(stranger.Id, contractId, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.OfferNotFound, (await schedules.SelectOfferAsync(stranger.Id, offer.OfferId, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.OfferNotFound, (await schedules.SelectOfferAsync(signed.Cart.CustomerId, Guid.NewGuid(), None)).ErrorCode);
        Assert.Equal(InstallationSlotOfferStatuses.Open, (await ctx.Db.InstallationSlotOffers.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_004_A_replaced_offer_cannot_be_selected_and_republishing_keeps_history()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var contractId = signed.Contract.ContractId;
        var old = (await schedules.PublishOffersAsync(signed.Cart.TechnicianId, contractId, [First], null, None)).Value!.Single();

        var renewed = await schedules.PublishOffersAsync(signed.Cart.TechnicianId, contractId, [Second], null, None);
        Assert.True(renewed.Succeeded, renewed.Message);

        Assert.Equal(InstallationScheduleErrorCodes.OfferNotOpen, (await schedules.SelectOfferAsync(signed.Cart.CustomerId, old.OfferId, None)).ErrorCode);
        var history = (await schedules.ListOffersForTechnicianAsync(signed.Cart.TechnicianId, contractId, None)).Value!;
        Assert.Equal(2, history.Count);
        Assert.Equal("REPLACED_BY_NEW_OFFERS", history.Single(o => o.OfferId == old.OfferId).WithdrawnReason);
        Assert.Equal(0, await ctx.Db.InstallationScheduleProposals.CountAsync());
    }

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_005_An_offer_that_became_impossible_is_shown_unavailable_and_cannot_be_selected()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var offer = (await schedules.PublishOffersAsync(signed.Cart.TechnicianId, signed.Contract.ContractId, [First], null, None)).Value!.Single();

        // The technician's leave recorded after the offer (an offer holds no capacity).
        ctx.Db.Add(new TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(), TechnicianUserId = signed.Cart.TechnicianId, StartsAt = At(7, 0, Day.AddDays(1)).ToUniversalTime(),
            EndsAt = At(18, 0, Day.AddDays(2)).ToUniversalTime(), Reason = "Annual leave", CreatedAt = ctx.Clock.GetUtcNow(),
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var seen = Assert.Single((await schedules.ListOffersForCustomerAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!);
        Assert.False(seen.Available);
        Assert.DoesNotContain("leave", seen.UnavailableReason!, StringComparison.OrdinalIgnoreCase);   // no internal detail for the customer
        var techView = Assert.Single((await schedules.ListOffersForTechnicianAsync(signed.Cart.TechnicianId, signed.Contract.ContractId, None)).Value!);
        Assert.False(techView.Available);

        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable, (await schedules.SelectOfferAsync(signed.Cart.CustomerId, offer.OfferId, None)).ErrorCode);
        // The clock passing the start also makes it unavailable.
        ctx.Db.TechnicianUnavailablePeriods.RemoveRange(ctx.Db.TechnicianUnavailablePeriods);
        await ctx.Db.SaveChangesAsync();
        ctx.Clock.Now = At(9, 0, Day.AddDays(1)).ToUniversalTime();
        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable, (await schedules.SelectOfferAsync(signed.Cart.CustomerId, offer.OfferId, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationScheduleProposals.CountAsync());
    }

    /// <summary>E2E-04 (unit level, SQLite): two customers of one technician selecting overlapping offers — only the first allocation succeeds.</summary>
    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_006_Two_customers_cannot_both_take_the_same_technician_interval()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var start = ctx.Clock.Now;
        var a = await SimplifiedFlowFixture.SignedAsync(ctx, "Customer A");
        var afterA = ctx.Clock.Now;
        ctx.Clock.Now = start;   // the second booking is made before the first survey day, like the first one
        var b = await SimplifiedFlowFixture.SignedAsync(ctx, "Customer B");
        ctx.Clock.Now = afterA > ctx.Clock.Now ? afterA : ctx.Clock.Now;
        var technician = a.Cart.TechnicianId;
        // The same technician is responsible for both contracts (latest completed survey report).
        foreach (var report in await ctx.Db.OnSiteSurveyReports.Where(r => r.SurveyRequestId == b.Cart.RequestId).ToListAsync()) report.TechnicianUserId = technician;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var schedules = ctx.Schedules();
        var offerA = (await schedules.PublishOffersAsync(technician, a.Contract.ContractId, [First], null, None)).Value!.Single();
        var offerB = (await schedules.PublishOffersAsync(technician, b.Contract.ContractId, [First], null, None)).Value!.Single();

        Assert.True((await schedules.SelectOfferAsync(a.Cart.CustomerId, offerA.OfferId, None)).Succeeded);
        var second = await schedules.SelectOfferAsync(b.Cart.CustomerId, offerB.OfferId, None);

        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable, second.ErrorCode);
        Assert.Equal(1, await ctx.Db.InstallationScheduleProposals.CountAsync());
        Assert.Equal(InstallationSlotOfferStatuses.Open, (await ctx.Db.InstallationSlotOffers.AsNoTracking().SingleAsync(o => o.Id == offerB.OfferId)).Status);
        // The technician no longer sees that interval as available for B, and cannot offer it again.
        Assert.False(Assert.Single((await schedules.ListOffersForTechnicianAsync(technician, b.Contract.ContractId, None)).Value!).Available);
        Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable,
            (await schedules.PublishOffersAsync(technician, b.Contract.ContractId, [First], null, None)).ErrorCode);
    }

    [Fact]
    public async Task INSTALL_SCHEDULE_OFFER_007_On_a_paid_contract_the_selection_replaces_the_plan_with_history_and_is_confirmed_at_once()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var schedules = ctx.Schedules();
        var contractId = signed.Contract.ContractId;
        Assert.True((await schedules.ProposeAsync(signed.Cart.TechnicianId, false, contractId, InstallationScheduleTests.Plan(First.StartsAt, First.EndsAt), None)).Succeeded);
        await InstallationScheduleTests.PayCashAsync(ctx, signed);

        var offer = (await schedules.PublishOffersAsync(signed.Cart.TechnicianId, contractId, [Second], null, None)).Value!.Single();
        var moved = await schedules.SelectOfferAsync(signed.Cart.CustomerId, offer.OfferId, None);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(InstallationScheduleStatuses.Confirmed, moved.Value!.Status);
        var history = (await schedules.GetForStaffAsync(contractId, None)).Value!;
        Assert.Equal(2, history.Count);
        Assert.StartsWith("REPLACED: Customer selected", history.Single(h => h.Status == InstallationScheduleStatuses.Cancelled).CancellationReason);
        var project = await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.ContractId == contractId);
        Assert.Equal(Second.StartsAt.ToUniversalTime(), project.ScheduledStartsAt!.Value.ToUniversalTime());
    }
}
