using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Admin-editable timing settings are wired into handlers and workers (spec2 §4) and are NOT retroactive: every timing event stores its
/// snapshot at creation, workers act only on persisted deadlines, and a new event uses the settings current when it happens.
/// </summary>
public sealed class SettingsWiringTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<Guid> AdminAsync(SchedulingTestContext ctx) => (await ctx.SeedStaffAsync("Settings admin", RoleCodes.Admin)).Id;

    private static async Task<SeededSurveyRequest> UncoveredRequestAsync(SchedulingTestContext ctx, string name)
        => await ctx.SeedSurveyRequestAsync(null, name, communeCode: BenThanh);

    // ------------------------------------------------------------------ dispatch SLA

    [Fact]
    public async Task An_existing_dispatch_request_keeps_its_review_deadline_after_an_admin_update_and_a_new_one_uses_the_new_SLA()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        await ctx.SeedStaffAsync("Manager", RoleCodes.Manager);
        var oldRequest = await UncoveredRequestAsync(ctx, "Old");
        var old = (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(oldRequest.Customer.Id, oldRequest.Request.Id, [], null), None)).Value!;
        var before = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == old.DispatchRequestId);

        var version = await ctx.UpdateSettingsAsync(admin, (DispatchReviewSlaMinutes, 30));
        var newRequest = await UncoveredRequestAsync(ctx, "New");
        var fresh = (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(newRequest.Customer.Id, newRequest.Request.Id, [], null), None)).Value!;

        var oldAfter = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == old.DispatchRequestId);
        var created = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == fresh.DispatchRequestId);
        Assert.Equal((240, before.ReviewDueAt, 0), (oldAfter.ReviewSlaMinutes, oldAfter.ReviewDueAt, oldAfter.SettingsVersion));
        Assert.Equal((30, ctx.Clock.Now.AddMinutes(30), version), (created.ReviewSlaMinutes, created.ReviewDueAt, created.SettingsVersion));

        // The worker uses the persisted deadlines only: after 30 minutes the new request is overdue, the old one is not.
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(30);
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(50, None)).MarkedOverdue);
        Assert.Null((await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == old.DispatchRequestId)).OverdueAt);
        Assert.NotNull((await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == fresh.DispatchRequestId)).OverdueAt);
        ctx.Clock.Now = before.ReviewDueAt;
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(50, None)).MarkedOverdue);
    }

    [Fact]
    public async Task An_existing_proposal_keeps_its_expiry_and_a_new_proposal_uses_the_new_response_window()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        var manager = await ctx.SeedStaffAsync("Manager", RoleCodes.Manager);
        var tech = await ctx.SeedTechnicianAsync("District 1 tech", District1);
        var r = await UncoveredRequestAsync(ctx, "Customer");
        var dispatch = (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(r.Customer.Id, r.Request.Id, [], null), None)).Value!;
        var start = At(9, date: Day.AddDays(5));
        var first = (await ctx.Dispatch().ProposeAsync(new DispatchTechnicianCommand(manager.Id, dispatch.DispatchRequestId, tech.Id, start, true, "Cross-zone", dispatch.Revision), None)).Value!;
        var firstProposal = await ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync();
        Assert.Equal((1440, ctx.Clock.Now.AddMinutes(1440)), (firstProposal.ResponseWindowMinutes, firstProposal.ExpiresAt));

        var version = await ctx.UpdateSettingsAsync(admin, (DispatchProposalResponseWindowMinutes, 120));
        Assert.Equal(firstProposal.ExpiresAt, (await ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync()).ExpiresAt);
        // The worker does not expire it at the new (shorter) window: only at its persisted expiry.
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(121);
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).ExpiredProposals);

        // A NEW proposal on the same (old) request uses the current window.
        var withdrawn = (await ctx.Dispatch().WithdrawProposalAsync(new WithdrawProposalCommand(manager.Id, dispatch.DispatchRequestId, firstProposal.Id, null), None)).Value!;
        await ctx.Dispatch().ProposeAsync(new DispatchTechnicianCommand(manager.Id, dispatch.DispatchRequestId, tech.Id, start, true, "Cross-zone", withdrawn.Request.Revision), None);
        var second = await ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync(p => p.Status == ProposalStatuses.Proposed);
        Assert.Equal((120, version, ctx.Clock.Now.AddMinutes(120)), (second.ResponseWindowMinutes, second.SettingsVersion, second.ExpiresAt));
        _ = first;
    }

    // ------------------------------------------------------------------ acceptance deadline

    [Fact]
    public async Task An_existing_acceptance_deadline_is_unchanged_by_an_update_and_new_bookings_and_releases_use_the_current_window()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var booked = await ctx.BookAsync(first, At(9));
        Assert.Equal((ctx.Clock.Now.AddMinutes(240), 240, 0), (booked.AcceptanceDeadlineAt!.Value, booked.AcceptanceWindowMinutes!.Value, booked.SettingsVersion!.Value));

        var version = await ctx.UpdateSettingsAsync(admin, (AcceptanceWindowMinutes, 60));
        Assert.Equal(booked.AcceptanceDeadlineAt, (await ctx.AppointmentAsync(booked.Id)).AcceptanceDeadlineAt);
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var newer = await ctx.BookAsync(second, At(13));
        Assert.Equal((ctx.Clock.Now.AddMinutes(60), 60, version), (newer.AcceptanceDeadlineAt!.Value, newer.AcceptanceWindowMinutes!.Value, newer.SettingsVersion!.Value));

        // The escalation worker acts on the persisted deadlines: at +60 min only the new booking escalates.
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(60);
        Assert.Equal(1, await ctx.Escalation().EscalateDueAsync(50, None));
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(booked.Id)).Status);
        Assert.Equal(AppointmentStatuses.Escalated, (await ctx.AppointmentAsync(newer.Id)).Status);

        // A release is a new timing event: its fresh deadline uses the current settings.
        Assert.True((await ctx.AcceptAsync(tech.Id, booked.Id)).Succeeded);
        var released = await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(tech.Id, booked.Id, "Sick"), None);
        Assert.True(released.Succeeded, released.Message);
        var after = await ctx.AppointmentAsync(booked.Id);
        Assert.Equal((ctx.Clock.Now.AddMinutes(60), 60, version), (after.AcceptanceDeadlineAt!.Value, after.AcceptanceWindowMinutes!.Value, after.SettingsVersion!.Value));
    }

    [Fact]
    public async Task Acceptance_reminders_are_disabled_by_default_and_once_enabled_one_is_recorded_per_open_appointment()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var r = await ctx.SeedSurveyRequestAsync(BinhTan);
        var booked = await ctx.BookAsync(r, At(9));
        ctx.Clock.Now = booked.AcceptanceDeadlineAt!.Value.AddMinutes(-10);
        Assert.Equal(0, await ctx.Escalation().SendAcceptanceRemindersAsync(50, None));   // 0 = disabled

        await ctx.UpdateSettingsAsync(admin, (AcceptanceReminderLeadMinutes, 30));
        Assert.Equal(1, await ctx.Escalation().SendAcceptanceRemindersAsync(50, None));
        Assert.Equal(0, await ctx.Escalation().SendAcceptanceRemindersAsync(50, None));   // deduplicated

        var appointment = await ctx.AppointmentAsync(booked.Id);
        Assert.Equal(ctx.Clock.Now, appointment.AcceptanceReminderSentAt);
        Assert.Equal(booked.Revision, appointment.Revision);                             // an acceptance in flight is not invalidated
        Assert.Equal(booked.AcceptanceDeadlineAt, appointment.AcceptanceDeadlineAt);
        var notification = Assert.Single(await ctx.Db.SchedulingNotifications.AsNoTracking().Where(n => n.Type == AppointmentEventTypes.AcceptanceReminder).ToListAsync());
        Assert.Equal(tech.Id, notification.RecipientUserId);
        Assert.True((await ctx.AcceptAsync(tech.Id, booked.Id, booked.Revision)).Succeeded);
    }

    // ------------------------------------------------------------------ booking limits, buffer, lead times

    [Fact]
    public async Task Booking_horizon_minimum_lead_and_buffer_come_from_the_current_settings()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var r = await ctx.SeedSurveyRequestAsync(BinhTan);
        Assert.True((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(r.Customer.Id, r.Site.Id, Day.AddDays(10)), None)).Succeeded);

        await ctx.UpdateSettingsAsync(admin, (BookingHorizonDays, 5), (MinimumBookingLeadMinutes, 60 * 28));
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(r.Customer.Id, r.Site.Id, Day.AddDays(10)), None)).ErrorCode);
        // now = Mon 06:00; +28 h = Tue 10:00: the Tuesday morning slots before 10:00 are gone.
        var tuesday = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(r.Customer.Id, r.Site.Id, Day), None)).Value!;
        Assert.All(tuesday.Slots, s => Assert.True(s.StartsAt > At(10)));
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime,
            (await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, At(8), null), None)).ErrorCode);

        // Buffer: with 90 minutes around a 13:00–14:30 booking, the last morning start is 10:00 (11:30 + 90 = 13:00) and no afternoon start fits.
        await ctx.UpdateSettingsAsync(admin, (MinimumBookingLeadMinutes, 0), (BufferBetweenAppointmentsMinutes, 90));
        await ctx.BookAsync(r, At(13));
        var other = await ctx.SeedSurveyRequestAsync(BinhTan, "Other");
        var slots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(other.Customer.Id, other.Site.Id, Day), None)).Value!;
        Assert.DoesNotContain(slots.Slots, s => s.StartsAt > At(10));
        Assert.Contains(slots.Slots, s => s.StartsAt == At(10));
        Assert.Equal(90, (await ctx.Schedule().HandleAsync(Guid.NewGuid(), Day, false, None)).BufferBetweenAppointmentsMinutes);
    }

    [Fact]
    public async Task Customer_change_lead_and_the_release_cut_off_come_from_the_current_settings()
    {
        await using var ctx = Create();
        var admin = await AdminAsync(ctx);
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var r = await ctx.SeedSurveyRequestAsync(BinhTan);
        var booked = await ctx.BookAsync(r, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, booked.Id)).Succeeded);

        // Start is Tue 09:00, now Mon 06:00 (27 h before). Lead 28 h: the customer can no longer change; 26 h: allowed again.
        await ctx.UpdateSettingsAsync(admin, (CustomerChangeLeadMinutes, 28 * 60));
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, (await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, null), None)).ErrorCode);
        Assert.False((await ctx.CustomerQueries().ListAsync(r.Customer.Id, None)).Single().Appointment!.CanCustomerChange);

        // Release cut-off = onsiteSurvey.earlyStartMinutes: 28 h before the start refuses the release.
        await ctx.UpdateSettingsAsync(admin, (CustomerChangeLeadMinutes, 26 * 60), (OnSiteSurveyEarlyStartMinutes, 1440));
        Assert.True((await ctx.CustomerQueries().ListAsync(r.Customer.Id, None)).Single().Appointment!.CanCustomerChange);
        ctx.Clock.Now = At(10, date: Day.AddDays(-1)).ToUniversalTime();               // 23 h before: inside the 24 h cut-off
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, (await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(tech.Id, booked.Id, "Sick"), None)).ErrorCode);
        await ctx.UpdateSettingsAsync(admin, (OnSiteSurveyEarlyStartMinutes, 60));
        Assert.True((await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(tech.Id, booked.Id, "Sick"), None)).Succeeded);
    }

    [Fact]
    public async Task On_site_early_start_and_the_installation_maximum_length_come_from_the_current_settings()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var admin = await AdminAsync(ctx.Scheduling);
        var paid = await SmartSolar.Tests.Unit.InstallationWorkflow.InstallationTests.PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        AssignTechnicianInput Days(int n) => new(tech.Id, At(8, 0, Day.AddDays(1)), At(12, 0, Day.AddDays(n)), null, project.Revision);

        await ctx.Scheduling.UpdateSettingsAsync(admin, (InstallationMaxAssignmentDays, 2));
        Assert.Equal(InstallationErrorCodes.InvalidSchedule, (await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Days(3), None)).ErrorCode);
        Assert.True((await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Days(2), None)).Succeeded);

        // The paid contract's survey appointment (Tue 10:00) is confirmed; the survey itself is done, so check the start rule on a fresh one.
        var survey = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Survey");
        var surveyTech = await ctx.Scheduling.SeedTechnicianAsync("Surveyor", BinhTan);
        var appointment = await ctx.Scheduling.BookAsync(survey, At(9, date: Day.AddDays(4)));
        Assert.True((await ctx.Scheduling.AcceptAsync(surveyTech.Id, appointment.Id)).Succeeded);
        ctx.Clock.Now = At(6, date: Day.AddDays(4)).ToUniversalTime();                  // 3 h before the start
        Assert.Equal(SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyErrorCodes.TooEarly, (await ctx.OnSite().StartAsync(surveyTech.Id, appointment.Id, None)).ErrorCode);
        await ctx.Scheduling.UpdateSettingsAsync(admin, (OnSiteSurveyEarlyStartMinutes, 240));
        Assert.True((await ctx.OnSite().StartAsync(surveyTech.Id, appointment.Id, None)).Succeeded);
    }
}
