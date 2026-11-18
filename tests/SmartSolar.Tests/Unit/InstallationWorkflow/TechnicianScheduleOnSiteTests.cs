using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;
using static SmartSolar.Tests.Unit.InstallationWorkflow.InstallationScheduleTests;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Technician-led scheduling (2026-11-18): once the contract exists and is paid (created from the paid quotation), the surveying technician consults
/// the backend's availability, agrees the date with the customer in person and records it. The backend checks the interval, the technician's
/// eligibility (zone, working calendar, leave, commitments) and conflicts with the technician's other agreed dates; the date is PLANNED and is
/// confirmed automatically when the signed contract becomes PAID. The customer only reads the schedule; nothing waits for the customer's app.
/// </summary>
public sealed class TechnicianScheduleOnSiteTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(SimplifiedFlowFixture.Cart Cart, UserAccount Manager, ContractView Contract)> PrepaidDraftAsync(InstallationWorkflowTestContext ctx,
        string customer = "Customer")
    {
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx, customer);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync($"Manager of {customer}");
        return (cart, manager, await SimplifiedFlowFixture.PaidOnSiteAsync(ctx, cart, manager));
    }

    /// <summary>Manager prepares and publishes the paid contract, uploads the paper scan signed on site and approves it (production follow-up wiring).</summary>
    private static async Task<ContractView> SignAsync(InstallationWorkflowTestContext ctx, UserAccount manager, ContractView draft)
    {
        var published = await SimplifiedFlowFixture.PublishedAsync(ctx, manager, draft);
        var scan = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.SignedContract, "pdf");
        var submitted = (await ctx.Contracts().SubmitPaperSignedDocumentAsync(manager.Id, true, published.ContractId, scan, published.OfficialDocument!.Sha256!,
            null, "signed on site", None)).Value!;
        var reviewed = await ctx.ContractsWithFollowUp().ReviewAsync(manager.Id, true, published.ContractId, submitted.Revision, ReviewDecision.Approve, null, None);
        Assert.True(reviewed.Succeeded, reviewed.Message);
        return reviewed.Value!;
    }

    [Fact]
    public async Task SCHEDULE_ONSITE_001_The_technician_records_the_agreed_date_on_a_paid_contract_and_it_is_confirmed_when_the_contract_completes()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, manager, draft) = await PrepaidDraftAsync(ctx);
        var schedules = ctx.Schedules();
        var technician = cart.TechnicianId;

        // Availability is open as soon as the contract exists and is paid (no SIGNED step needed first).
        var availability = await schedules.AvailabilityAsync(technician, draft.ContractId, Day.AddDays(1), Day.AddDays(7), 2, None);
        Assert.True(availability.Succeeded, availability.Message);
        Assert.Contains(availability.Value!, a => a.Available);

        var planned = await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(note: "Thống nhất trực tiếp với khách tại nhà 20/10"), None);
        Assert.True(planned.Succeeded, planned.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, false, false, true),
            (planned.Value!.Status, planned.Value.IsConfirmed, planned.Value.AwaitingPayment, planned.Value.AwaitingContractSignature));
        Assert.Equal(technician, (await ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync()).CreatedByUserId);
        // The customer reads it; nothing to select or confirm.
        var seen = Assert.Single((await schedules.GetForCustomerAsync(cart.CustomerId, draft.ContractId, None)).Value!);
        Assert.Equal(planned.Value.ScheduleId, seen.ScheduleId);

        // The signed contract becomes PAID at once (paid before it existed) and the agreed date is confirmed without anyone else.
        var completed = await SignAsync(ctx, manager, draft);
        Assert.Equal(ContractStatuses.Paid, completed.Status);
        var confirmed = (await schedules.GetForCustomerAsync(cart.CustomerId, draft.ContractId, None)).Value!.First();
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (confirmed.Status, confirmed.IsConfirmed));
        var assignment = await ctx.Db.InstallationAssignments.AsNoTracking().SingleAsync();
        Assert.Equal((technician, AssignmentStatuses.Accepted), (assignment.TechnicianUserId, assignment.Status));
    }

    [Fact]
    public async Task SCHEDULE_ONSITE_002_Invalid_or_conflicting_dates_are_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _, draft) = await PrepaidDraftAsync(ctx);
        var schedules = ctx.Schedules();
        var technician = cart.TechnicianId;

        Assert.Equal(InstallationScheduleErrorCodes.AgreementRequired, (await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(note: " "), None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.InvalidSchedule,
            (await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(At(8, 0, Day.AddDays(-2)), At(12, 0, Day.AddDays(-2))), None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.InvalidSchedule,
            (await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(At(12, 0, Day.AddDays(2)), At(8, 0, Day.AddDays(1))), None)).ErrorCode);
        Assert.True((await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(), None)).Succeeded);
        // A change needs a reason.
        Assert.Equal(InstallationScheduleErrorCodes.ReasonRequired,
            (await schedules.ProposeAsync(technician, false, draft.ContractId, Plan(At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3))), None)).ErrorCode);

        // A second customer of the same technician: an overlapping agreed date is refused, another interval is fine.
        ctx.Clock.Now = DefaultNow.ToUniversalTime();   // each journey books its survey on the fixture day
        var second = await SimplifiedFlowFixture.IssuedAsync(ctx, "Second customer");
        await ctx.Db.OnSiteSurveyReports.Where(r => r.SurveyRequestId == second.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TechnicianUserId, technician));
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager 2");
        var otherContract = await SimplifiedFlowFixture.PaidOnSiteAsync(ctx, second with { Survey = second.Survey with { Technician = cart.Survey.Technician } }, manager);
        var conflict = await schedules.ProposeAsync(technician, false, otherContract.ContractId, Plan(), None);
        Assert.Equal(InstallationScheduleErrorCodes.TechnicianIneligible, conflict.ErrorCode);
        Assert.Contains("PLANNED_CONFLICT", System.Text.Json.JsonSerializer.Serialize(conflict.Details));
        Assert.True((await schedules.ProposeAsync(technician, false, otherContract.ContractId, Plan(At(8, 0, Day.AddDays(5)), At(12, 0, Day.AddDays(5))), None)).Succeeded);
    }

    [Fact]
    public async Task SCHEDULE_ONSITE_003_Only_the_surveying_technician_schedules_and_only_a_paid_or_signed_contract()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _, draft) = await PrepaidDraftAsync(ctx);
        var schedules = ctx.Schedules();
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Other technician", BinhTan);

        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.ProposeAsync(stranger.Id, false, draft.ContractId, Plan(), None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.AvailabilityAsync(stranger.Id, draft.ContractId, Day.AddDays(1), Day.AddDays(2), 1, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.ProposeAsync(cart.CustomerId, false, draft.ContractId, Plan(), None)).ErrorCode);

        // A contract of the previous flow that is neither signed nor paid cannot be scheduled yet.
        ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var legacy = await SimplifiedFlowFixture.IssuedAsync(ctx, "Legacy customer");
        var legacyDraft = await SimplifiedFlowFixture.LegacyAcceptAsync(ctx, legacy);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotSigned, (await schedules.ProposeAsync(legacy.TechnicianId, false, legacyDraft.ContractId, Plan(), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationScheduleProposals.CountAsync());
    }

    [Fact]
    public async Task SCHEDULE_ONSITE_004_An_open_offer_of_the_retired_customer_selection_is_withdrawn_when_the_technician_records_the_date()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _, draft) = await PrepaidDraftAsync(ctx);
        var now = ctx.Clock.GetUtcNow();
        ctx.Db.InstallationSlotOffers.Add(new InstallationSlotOffer
        {
            Id = Guid.NewGuid(), ContractId = draft.ContractId, TechnicianUserId = cart.TechnicianId, StartsAt = At(8, 0, Day.AddDays(4)), EndsAt = At(12, 0, Day.AddDays(4)),
            Status = InstallationSlotOfferStatuses.Open, CreatedByUserId = cart.TechnicianId, CreatedAt = now, UpdatedAt = now,
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        Assert.True((await ctx.Schedules().ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(), None)).Succeeded);
        var offer = await ctx.Db.InstallationSlotOffers.AsNoTracking().SingleAsync();
        Assert.Equal((InstallationSlotOfferStatuses.Withdrawn, "REPLACED_BY_TECHNICIAN_SCHEDULE"), (offer.Status, offer.WithdrawnReason));
    }
}
