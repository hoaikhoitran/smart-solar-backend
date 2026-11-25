using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Quotations;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// FE Flow 1–4 fixes reported on 2026-11-25 (production handlers, SQLite). BUG numbers refer to guides/fe-flow-1-4-fixes/FE_HANDOFF.md.
/// </summary>
public sealed class FeFlowFixesTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Mon = Day.AddDays(-1);

    private static async Task<string?> ReasonAsync(InstallationWorkflowTestContext ctx, Guid technician, DateTimeOffset start, DateTimeOffset end)
        => (await ctx.Scheduling.Eligibility().CheckAsync(technician, new EligibilityRequest(start.ToUniversalTime(), end.ToUniversalTime(), null), None)).Reason;

    // ================================================================== BUG-01

    [Fact]
    public async Task BUG01_a_survey_completed_the_day_before_its_slot_frees_the_slot_and_still_counts_on_its_scheduled_day()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        ctx.Scheduling.OnSiteOptions.EarlyStartMinutes = 1440;   // a site that allows starting up to a day early (system setting, max 1440)
        var (__, a) = await EarlyCompletionSchedulingTests.SurveyAsync(ctx);   // Tue 10:00–11:30
        await EarlyCompletionSchedulingTests.CompleteAsync(ctx, a, At(15, 0, Mon), At(15, 56, Mon));
        var stored = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
        Assert.Equal((At(10).ToUniversalTime(), At(11, 30).ToUniversalTime(), At(15, 56, Mon).ToUniversalTime()),
            (stored.StartsAt.ToUniversalTime(), stored.EndsAt.ToUniversalTime(), stored.CompletedAt!.Value.ToUniversalTime()));

        // The original Tuesday 10:00 slot is bookable again (same technician: the only one of the zone).
        var booked = await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(10));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");

        // Daily limit (approved): the completed survey still counts on Tuesday, not on Monday.
        var range = await ctx.Scheduling.Eligibility().LoadDaysRangeAsync(Mon, Day, new TechnicianScope([a.Technician.Id], SchedulableOnly: false), None);
        Assert.True(range[Day].Single().Appointments.Single(s => s.AppointmentId == a.AppointmentId).CountsTowardDailyLimit);
        Assert.False(range[Mon].Single().Appointments.Single(s => s.AppointmentId == a.AppointmentId).CountsTowardDailyLimit);
        var single = await ctx.Scheduling.Eligibility().LoadDaysAsync(Mon, new TechnicianScope([a.Technician.Id], SchedulableOnly: false), None);
        Assert.False(single.Single().Appointments.Single(s => s.AppointmentId == a.AppointmentId).CountsTowardDailyLimit);
        // The real completion keeps the configured buffer on Monday.
        var buffer = (await ctx.Scheduling.Policies.GetAsync(None)).BufferBetweenAppointmentsMinutes;
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(15, 56, Mon).AddMinutes(buffer - 10), At(16, 50, Mon)));
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(15, 56, Mon).AddMinutes(buffer), At(17, 0, Mon)));
    }

    [Fact]
    public async Task BUG01_completing_before_the_allowed_start_or_as_another_technician_is_refused()
    {
        var (ctx, a) = await EarlyCompletionSchedulingTests.SurveyAsync();   // default early start: 120 minutes
        await using var _ = ctx;
        ctx.Clock.Now = At(15, 0, Mon).ToUniversalTime();
        Assert.Equal(OnSiteSurveyErrorCodes.TooEarly, (await ctx.OnSite().StartAsync(a.Technician.Id, a.AppointmentId, None)).ErrorCode);
        var view = await EarlyCompletionSchedulingTests.StartAsync(ctx, a, At(9));
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", BinhTan);
        Assert.Equal(OnSiteSurveyErrorCodes.AppointmentNotFound,
            (await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(stranger.Id, a.AppointmentId, view.Revision), None)).ErrorCode);
        // Still active (not completed): the slot keeps blocking.
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(10), At(11, 30)));
    }

    // ================================================================== BUG-02

    [Fact]
    public async Task BUG02_plans_of_completed_projects_never_block_while_active_plans_still_do()
    {
        var i = await EarlyCompletionSchedulingTests.InProgressAsync();
        await using var _ = i.Ctx;
        var plan = await i.Ctx.Db.InstallationScheduleProposals.SingleAsync(p => p.ContractId == i.Flow.ContractId);
        var uow = new InstallationUnitOfWork(i.Ctx.Db);
        DateTimeOffset start = plan.StartsAt, end = plan.EndsAt;

        // Synthetic stored state (as an older row may hold it): the same plan PLANNED while the project is IN_PROGRESS → it blocks.
        plan.Status = InstallationScheduleStatuses.Planned;
        await i.Ctx.Db.SaveChangesAsync();
        i.Ctx.Db.ChangeTracker.Clear();
        Assert.Single(await uow.ListOverlappingLiveProposalsAsync(i.TechnicianId, Guid.NewGuid(), start, end, None));

        // The customer accepts → project COMPLETED: its plan (whatever status it stores) no longer blocks; the row is kept unchanged.
        i.Ctx.Clock.Now = SchedulingTestContext.At(11, 0, Day.AddDays(1)).ToUniversalTime();
        Assert.True((await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Done", null, None)).Succeeded);
        await PaymentPlanFlow.AcceptAsync(i.Ctx, i.Flow, i.ProjectId);
        Assert.Empty(await uow.ListOverlappingLiveProposalsAsync(i.TechnicianId, Guid.NewGuid(), start, end, None));
        Assert.Equal(InstallationScheduleStatuses.Planned, (await i.Ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).Status);
        // A CONFIRMED plan is represented by its assignment (COMPLETED now): not busy either.
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, start, start.AddHours(2)));
    }

    // ================================================================== BUG-03

    private static async Task<(SimplifiedFlowFixture.Cart Cart, InstallationWorkflowTestContext Ctx)> DraftAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        return (await SimplifiedFlowFixture.DraftAsync(ctx), ctx);
    }

    private static Dictionary<string, string[]> Errors(object? details) => Assert.IsType<Dictionary<string, string[]>>(details);

    [Fact]
    public async Task BUG03_panels_and_inverters_need_whole_quantities_on_add_and_update_while_services_keep_decimals()
    {
        var (cart, ctx) = await DraftAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        var id = cart.Quotation.QuotationId;
        var halfPanel = await q.AddCartLineAsync(cart.TechnicianId, id, cart.Quotation.Revision, cart.Catalog.Panel.Id, 1.5m, None);
        Assert.Equal(CommonErrorCodes.ValidationFailed, halfPanel.ErrorCode);
        Assert.Contains("whole", Errors(halfPanel.Details)["quantity"][0]);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.AddCartLineAsync(cart.TechnicianId, id, cart.Quotation.Revision, cart.Catalog.Inverter.Id, 1.5m, None)).ErrorCode);
        Assert.Empty(await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == id).ToListAsync());

        var panels = await q.AddCartLineAsync(cart.TechnicianId, id, cart.Quotation.Revision, cart.Catalog.Panel.Id, 2m, None);
        Assert.True(panels.Succeeded, panels.Message);
        Assert.Equal(6_000_000m, panels.Value!.Lines.Single().LineTotal);   // 2 × 3,000,000 (server price)
        var labor = await q.AddCartLineAsync(cart.TechnicianId, id, panels.Value.Revision, cart.Catalog.Labor.Id, 1.5m, None);
        Assert.True(labor.Succeeded, labor.Message);   // a service keeps its decimal quantity
        var panelLine = labor.Value!.Lines.Single(l => l.ProductId == cart.Catalog.Panel.Id);
        var update = await q.UpdateCartLineAsync(cart.TechnicianId, id, panelLine.LineId, labor.Value.Revision, 2.5m, None);
        Assert.Equal(CommonErrorCodes.ValidationFailed, update.ErrorCode);
        Assert.True(Errors(update.Details).ContainsKey("quantity"));
        Assert.Equal(2m, (await ctx.Db.QuotationLines.AsNoTracking().SingleAsync(l => l.Id == panelLine.LineId)).Quantity);
    }

    [Fact]
    public async Task BUG03_a_legacy_draft_with_fractional_equipment_cannot_be_issued()
    {
        var (cart, ctx) = await DraftAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        var id = cart.Quotation.QuotationId;
        var view = (await q.AddCartLineAsync(cart.TechnicianId, id, cart.Quotation.Revision, cart.Catalog.Panel.Id, 2m, None)).Value!;
        view = (await q.AddCartLineAsync(cart.TechnicianId, id, view.Revision, cart.Catalog.Inverter.Id, 1m, None)).Value!;
        // Stored before the rule existed (synthetic legacy row): 1.5 panels.
        var line = await ctx.Db.QuotationLines.SingleAsync(l => l.QuotationId == id && l.ProductId == cart.Catalog.Panel.Id);
        line.Quantity = 1.5m;
        line.LineTotal = 4_500_000m;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var issued = await q.IssueCartAsync(cart.TechnicianId, id, view.Revision, None);
        Assert.Equal(CommonErrorCodes.ValidationFailed, issued.ErrorCode);
        Assert.Contains("SOLAR_PANEL", Assert.Single(Errors(issued.Details)["quantity"]));
        Assert.Equal(SmartSolar.Modules.Quotations.QuotationStatuses.Draft, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
    }

    // ================================================================== BUG-04

    [Fact]
    public async Task BUG04_every_quotation_read_returns_the_live_contract_id_and_a_discarded_contract_is_not_shown()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var quotationId = flow.Cart.Quotation.QuotationId;

        var detail = await ctx.Quotations().GetForTechnicianAsync(flow.TechnicianId, quotationId, None);
        Assert.Equal(flow.ContractId, detail.Value!.ContractId);
        var list = await ctx.QuotationsWithPaymentPlan().ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None);
        Assert.Equal(flow.ContractId, Assert.Single(list.Value!, v => v.QuotationId == quotationId).ContractId);
        // A fresh handler (reload) still returns it: it comes from the persisted quotation → contract link.
        Assert.Equal(flow.ContractId, (await ctx.QuotationsWithPaymentPlan().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
        Assert.Equal(flow.ContractId, Assert.Single((await ctx.Quotations().ListForSurveyRequestAsync(flow.Manager.Id, true, flow.Cart.RequestId, None)).Value!).ContractId);
        // Ownership unchanged: another technician sees nothing.
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", BinhTan);
        Assert.Equal(QuotationErrorCodes.NotFound, (await ctx.Quotations().GetForTechnicianAsync(stranger.Id, quotationId, None)).ErrorCode);
        Assert.False((await ctx.Quotations().ListForTechnicianAsync(stranger.Id, flow.Cart.RequestId, None)).Succeeded);

        // The customer withdraws before the deposit: the contract is DISCARDED and no longer reported (existing visibility rule).
        var current = await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.True((await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, quotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Declined, current.Revision, "Changed their mind"), None)).Succeeded);
        Assert.Null((await ctx.Quotations().GetForTechnicianAsync(flow.TechnicianId, quotationId, None)).Value!.ContractId);
        Assert.Null(Assert.Single((await ctx.Quotations().ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None)).Value!).ContractId);
    }
}
