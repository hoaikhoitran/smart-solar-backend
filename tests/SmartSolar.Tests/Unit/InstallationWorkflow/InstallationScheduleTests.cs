using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// 2026-11-17: after the contract is SIGNED the surveying technician records the installation date agreed with the customer (PLANNED, no
/// capacity held, never shown as confirmed); when the contract is PAID the system creates the single project, re-checks the technician under the
/// locks and confirms (CONFIRMED) or flags RESCHEDULE_REQUIRED. Work still starts only with an accepted assignment and every prerequisite.
/// </summary>
public sealed class InstallationScheduleTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static DateTimeOffset Start => At(8, 0, Day.AddDays(1));
    private static DateTimeOffset End => At(12, 0, Day.AddDays(2));

    internal static ProposeInstallationScheduleInput Plan(DateTimeOffset? start = null, DateTimeOffset? end = null, string? note = "Khách đồng ý qua điện thoại 19/10 15:00",
        string? reason = null, int? revision = null)
        => new(null, start ?? Start, end ?? End, note, reason, revision);

    /// <summary>Pays the SIGNED contract in cash through the production wiring (the schedule follow-up runs after the commit).</summary>
    internal static async Task PayCashAsync(InstallationWorkflowTestContext ctx, SimplifiedFlowFixture.Signed signed)
    {
        var payments = ctx.PaymentsWithSchedule();
        Assert.True((await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);
        var paid = await payments.ConfirmCashAsync(signed.Manager.Id, signed.Contract.ContractId,
            new(signed.Contract.TotalAmount, $"PT-{Guid.NewGuid():N}"[..12], evidence, null), None);
        Assert.True(paid.Succeeded, paid.Message);
    }

    [Fact]
    public async Task Signed_contract_technician_plans_the_agreed_date_and_cash_payment_confirms_it_once_without_a_manager()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        var schedules = ctx.Schedules();

        var planned = await schedules.ProposeAsync(signed.Cart.TechnicianId, false, contractId, Plan(), None);
        Assert.True(planned.Succeeded, planned.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, false, true), (planned.Value!.Status, planned.Value.IsConfirmed, planned.Value.AwaitingPayment));
        Assert.Equal(signed.Cart.TechnicianId, planned.Value.TechnicianUserId);
        // No project, no assignment, no capacity hold before payment; the customer is told it is not confirmed yet.
        Assert.Equal(0, await ctx.Db.InstallationProjects.CountAsync());
        Assert.Equal(0, await ctx.Db.InstallationAssignments.CountAsync());
        var note = await ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == signed.Cart.CustomerId
            && n.Type == CustomerNotificationTypes.InstallationSchedulePlanned).SingleAsync();
        Assert.Contains("chỉ được xác nhận sau khi hợp đồng được thanh toán", note.Message);

        // The customer (mobile) sees PLANNED, not CONFIRMED.
        var mine = Assert.Single(await schedules.ListForCustomerAsync(signed.Cart.CustomerId, None));
        Assert.Equal((InstallationScheduleStatuses.Planned, false), (mine.Status, mine.IsConfirmed));
        Assert.Equal(Start, mine.StartsAt);

        // The same plan again is idempotent.
        var again = await schedules.ProposeAsync(signed.Cart.TechnicianId, false, contractId, Plan(), None);
        Assert.Equal(planned.Value.ScheduleId, again.Value!.ScheduleId);

        // PAID in cash: the project is created by the system, the assignment re-checked and accepted, the schedule CONFIRMED.
        await PayCashAsync(ctx, signed);
        var project = await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.ContractId == contractId);
        Assert.Null(project.ManagerUserId);
        Assert.Equal((InstallationStatuses.Scheduled, signed.Cart.TechnicianId, Start.ToUniversalTime(), End.ToUniversalTime()),
            (project.Status, project.ResponsibleTechnicianUserId!.Value, project.ScheduledStartsAt!.Value, project.ScheduledEndsAt!.Value));
        var assignment = await ctx.Db.InstallationAssignments.AsNoTracking().SingleAsync(a => a.ProjectId == project.Id);
        Assert.Equal((AssignmentStatuses.Accepted, signed.Cart.TechnicianId), (assignment.Status, assignment.AssignedByUserId));
        var confirmed = (await schedules.GetForCustomerAsync(signed.Cart.CustomerId, contractId, None)).Value!.First();
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true, project.Id, false), (confirmed.Status, confirmed.IsConfirmed, confirmed.ProjectId!.Value, confirmed.AwaitingPayment));
        Assert.Contains(await ctx.Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == project.Id).Select(e => e.EventType).ToListAsync(),
            t => t == InstallationEventTypes.ProjectCreated);

        // A repeated follow-up / sweep / manual retry never duplicates anything.
        await schedules.ConfirmForContractAsync(contractId, None);
        await schedules.SweepAsync(25, None);
        Assert.Equal((1, 1), (await ctx.Db.InstallationProjects.CountAsync(), await ctx.Db.InstallationAssignments.CountAsync()));
        Assert.Equal(1, await ctx.Db.InstallationScheduleProposals.CountAsync());

        // A Manager creating the project by hand afterwards is refused (one project per contract).
        Assert.Equal(InstallationErrorCodes.ProjectExists, (await ctx.Installations().CreateProjectAsync(signed.Manager.Id, contractId, None)).ErrorCode);

        // Booking is not starting: the prerequisites (EVN / PCCC / structural) still gate the start.
        ctx.Clock.Now = At(8, 30, Day.AddDays(1)).ToUniversalTime();
        var start = await ctx.Installations().StartProjectAsync(signed.Cart.TechnicianId, project.Id, None);
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, start.ErrorCode);
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, project.Id, signed.Manager.Id);
        Assert.True((await ctx.Installations().StartProjectAsync(signed.Cart.TechnicianId, project.Id, None)).Succeeded);
    }

    [Fact]
    public async Task A_conflict_at_payment_time_is_never_confirmed_the_plan_needs_a_new_date_and_a_new_date_confirms_at_once()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        var technician = signed.Cart.TechnicianId;
        Assert.True((await ctx.Schedules().ProposeAsync(technician, false, contractId, Plan(), None)).Succeeded);

        // PLANNED holds nothing: the technician's leave can be recorded over it, and makes it impossible.
        ctx.Db.Add(new TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(), TechnicianUserId = technician, StartsAt = At(7, 0, Day.AddDays(1)).ToUniversalTime(), EndsAt = At(18, 0, Day.AddDays(2)).ToUniversalTime(),
            Reason = "Annual leave", CreatedAt = ctx.Clock.GetUtcNow(),
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        await PayCashAsync(ctx, signed);
        var project = await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.ContractId == contractId);
        Assert.Equal((InstallationStatuses.PendingAssignment, (Guid?)null), (project.Status, project.ResponsibleTechnicianUserId));
        Assert.Equal(0, await ctx.Db.InstallationAssignments.CountAsync());
        var plan = (await ctx.Schedules().GetForTechnicianAsync(technician, contractId, None)).Value!.Single();
        Assert.Equal((InstallationScheduleStatuses.RescheduleRequired, InstallationScheduleRescheduleReasons.TechnicianUnavailable, false),
            (plan.Status, plan.RescheduleReasonCode, plan.IsConfirmed));
        Assert.True(await ctx.Db.AfterSalesNotifications.AnyAsync(n => n.RecipientUserId == signed.Cart.CustomerId
            && n.Type == CustomerNotificationTypes.InstallationScheduleRescheduleRequired));

        // A new date needs a reason; with one, the PAID contract is confirmed immediately (history kept).
        var after = (At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)));
        Assert.Equal(InstallationScheduleErrorCodes.ReasonRequired,
            (await ctx.Schedules().ProposeAsync(technician, false, contractId, Plan(after.Item1, after.Item2), None)).ErrorCode);
        var moved = await ctx.Schedules().ProposeAsync(technician, false, contractId, Plan(after.Item1, after.Item2, reason: "Kỹ thuật viên nghỉ phép"), None);
        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(InstallationScheduleStatuses.Confirmed, moved.Value!.Status);
        var history = (await ctx.Schedules().GetForStaffAsync(contractId, None)).Value!;
        Assert.Equal(2, history.Count);
        Assert.StartsWith("REPLACED:", history.Single(h => h.Status == InstallationScheduleStatuses.Cancelled).CancellationReason);
        Assert.Equal(InstallationStatuses.Scheduled, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == project.Id)).Status);
    }

    [Fact]
    public async Task Only_the_surveying_technician_of_a_signed_contract_may_plan_with_an_agreement_and_a_valid_interval()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        var draft = await SimplifiedFlowFixture.AcceptAsync(ctx, cart);
        var schedules = ctx.Schedules();
        var other = await ctx.Scheduling.SeedTechnicianAsync("Other tech", BinhTan);

        // Not SIGNED yet.
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotSigned, (await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(), None)).ErrorCode);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var published = await SimplifiedFlowFixture.PublishedAsync(ctx, manager, draft);
        var scan = await ctx.VerifiedFileAsync(cart.CustomerId, FilePurposes.SignedContract, "pdf");
        var submitted = await ctx.Contracts().SubmitSignedDocumentAsync(cart.CustomerId, published.ContractId, scan, published.OfficialDocument!.Sha256!, None);
        Assert.True((await ctx.Contracts().ReviewAsync(manager.Id, true, published.ContractId, submitted.Value!.Revision, ReviewDecision.Approve, null, None)).Succeeded);

        // Another technician (or an unknown contract) sees nothing: 404.
        var foreign = await schedules.ProposeAsync(other.Id, false, draft.ContractId, Plan(), None);
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, foreign.ErrorCode);
        Assert.Equal(404, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(foreign.ErrorCode!));
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.GetForTechnicianAsync(other.Id, draft.ContractId, None)).ErrorCode);
        Assert.Empty(await schedules.ListForTechnicianAsync(other.Id, None));
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.ProposeAsync(cart.TechnicianId, false, Guid.NewGuid(), Plan(), None)).ErrorCode);

        // Business rules: the customer's agreement is recorded, the interval is valid and inside the technician's working time.
        var noAgreement = await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(note: " "), None);
        Assert.Equal(InstallationScheduleErrorCodes.AgreementRequired, noAgreement.ErrorCode);
        Assert.Equal(400, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(noAgreement.ErrorCode!));
        Assert.Equal(InstallationScheduleErrorCodes.InvalidSchedule, (await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId,
            Plan(At(8, 0, Day.AddDays(-2)), At(12, 0, Day.AddDays(-2))), None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.InvalidSchedule, (await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(End, Start), None)).ErrorCode);
        var night = await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(At(21, 0, Day.AddDays(1)), At(23, 0, Day.AddDays(1))), None);
        Assert.Equal(InstallationScheduleErrorCodes.TechnicianIneligible, night.ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationScheduleProposals.CountAsync());

        // The customer of another contract cannot read or change it; a stale revision is refused.
        var planned = (await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId, Plan(), None)).Value!;
        var stranger = await ctx.Scheduling.Inner.SeedUserAsync("Stranger");
        Assert.Equal(InstallationScheduleErrorCodes.ContractNotFound, (await schedules.GetForCustomerAsync(stranger.Id, draft.ContractId, None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.NotFound, (await schedules.RequestChangeAsync(stranger.Id, planned.ScheduleId, "Đổi ngày", None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.NotFound, (await schedules.CancelAsync(other.Id, false, planned.ScheduleId, "x", None)).ErrorCode);
        Assert.Equal(InstallationScheduleErrorCodes.StaleRevision, (await schedules.ProposeAsync(cart.TechnicianId, false, draft.ContractId,
            Plan(At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)), reason: "x", revision: planned.Revision + 5), None)).ErrorCode);

        // The customer asks for another date: recorded on the plan (still PLANNED), the technician sees it.
        Assert.Equal(InstallationScheduleErrorCodes.ReasonRequired, (await schedules.RequestChangeAsync(cart.CustomerId, planned.ScheduleId, " ", None)).ErrorCode);
        var change = await schedules.RequestChangeAsync(cart.CustomerId, planned.ScheduleId, "Tuần sau giúp tôi", None);
        Assert.True(change.Succeeded, change.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, "Tuần sau giúp tôi"), (change.Value!.Status, change.Value.CustomerChangeRequestNote));
        var seen = Assert.Single(await schedules.ListForTechnicianAsync(cart.TechnicianId, None)).Schedule!;
        Assert.NotNull(seen.CustomerChangeRequestedAt);

        // Cancellation needs a reason; then nothing is live.
        Assert.Equal(InstallationScheduleErrorCodes.ReasonRequired, (await schedules.CancelAsync(cart.TechnicianId, false, planned.ScheduleId, null, None)).ErrorCode);
        var cancelled = await schedules.CancelAsync(cart.TechnicianId, false, planned.ScheduleId, "Khách hoãn lắp đặt", None);
        Assert.Equal(InstallationScheduleStatuses.Cancelled, cancelled.Value!.Status);
        Assert.Empty(await schedules.ListForCustomerAsync(cart.CustomerId, None));
    }

    [Fact]
    public async Task A_plan_whose_start_passes_before_payment_needs_a_new_date_and_a_manager_assignment_supersedes_a_plan()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        Assert.True((await ctx.Schedules().ProposeAsync(signed.Cart.TechnicianId, false, contractId, Plan(), None)).Succeeded);

        ctx.Clock.Now = At(9, 0, Day.AddDays(1)).ToUniversalTime();   // unpaid, start passed
        var (confirmed, expired) = await ctx.Schedules().SweepAsync(25, None);
        Assert.Equal((0, 1), (confirmed, expired));
        var plan = (await ctx.Schedules().GetForStaffAsync(contractId, None)).Value!.Single();
        Assert.Equal((InstallationScheduleStatuses.RescheduleRequired, InstallationScheduleRescheduleReasons.InvalidSchedule), (plan.Status, plan.RescheduleReasonCode));

        // Paid later: nothing is auto-confirmed from a RESCHEDULE_REQUIRED plan; a Manager assigns by hand and the plan is closed as superseded.
        await PayCashAsync(ctx, signed);
        Assert.Equal(0, await ctx.Db.InstallationProjects.CountAsync());
        var project = (await ctx.Installations().CreateProjectAsync(signed.Manager.Id, contractId, None)).Value!;
        var assigned = await ctx.Installations().AssignAsync(signed.Manager.Id, project.ProjectId,
            new AssignTechnicianInput(signed.Cart.TechnicianId, At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)), null, project.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        var closed = (await ctx.Schedules().GetForStaffAsync(contractId, None)).Value!.Single();
        Assert.Equal(InstallationScheduleStatuses.Cancelled, closed.Status);
        Assert.StartsWith("SUPERSEDED:", closed.CancellationReason);
    }

    [Fact]
    public async Task Payment_by_PayOS_confirms_the_plan_through_the_same_follow_up()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        Assert.True((await ctx.Schedules().ProposeAsync(signed.Cart.TechnicianId, false, signed.Contract.ContractId, Plan(), None)).Succeeded);
        var payments = ctx.PaymentsWithSchedule();
        var attempt = await payments.CreateOrGetAttemptAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None);
        Assert.True(attempt.Succeeded, attempt.Message);
        var stored = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Value!.PaymentAttemptId);
        var paid = await payments.ConfirmAsync(stored.Id, (long)stored.Amount, "FT-1", ctx.Clock.GetUtcNow(), "WEBHOOK", None);
        Assert.Equal(Modules.Payments.PaymentStatuses.Paid, paid!.Status);
        var plan = (await ctx.Schedules().GetForStaffAsync(signed.Contract.ContractId, None)).Value!.Single();
        Assert.Equal(InstallationScheduleStatuses.Confirmed, plan.Status);
        // The duplicate webhook changes nothing.
        await payments.ConfirmAsync(stored.Id, (long)stored.Amount, "FT-1", ctx.Clock.GetUtcNow(), "WEBHOOK", None);
        Assert.Equal((1, 1), (await ctx.Db.InstallationProjects.CountAsync(), await ctx.Db.InstallationAssignments.CountAsync()));
    }
}
