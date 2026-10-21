using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Scheduling guards under concurrency and background changes (area deactivation guard, background-change retries,
/// re-homing on the reschedule keep-technician path, out-of-area acceptance, admin hold re-reads and daily cap, locked
/// Sales owner change, release deadline) plus restored coverage. Geography is SYNTHETIC.
/// </summary>
public sealed class SchedulingGuardTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Handlers over one (optionally proxied) unit of work, so an interleaving can be injected at an exact call.</summary>
    private sealed class Handlers(SchedulingTestContext ctx, ISchedulingUnitOfWork u)
    {
        private TechnicianEligibility Eligibility => new(u, ctx.Calendar, ctx.Options, ctx.Policies);
        private AppointmentAssignmentService Assignments => new(u, Eligibility, ctx.Options, ctx.Clock, ctx.DispatchStore);
        private AppointmentMover Mover => new(u, new AppointmentReservationService(u, Eligibility, ctx.Options, ctx.Clock), Assignments, ctx.Options, ctx.Clock);

        public CancelSurveyAppointmentHandler Cancel => new(u, ctx.Calendar, ctx.Options, ctx.Policies, ctx.Clock);
        public RescheduleSurveyAppointmentHandler Reschedule => new(u, Mover, Eligibility, ctx.Coverage(u), ctx.Calendar, ctx.Clock);
        public AdminTechnicianHandlers Admin => new(u, Assignments, ctx.Calendar, ctx.Options, ctx.Clock);
        public SalesOwnerHandler SalesOwner => new(u, ctx.Clock);
    }

    /// <summary>Runs <paramref name="change"/> once, right before the first transaction of the handler starts (after its reads).</summary>
    private static ISchedulingUnitOfWork BeforeFirstTransaction(SchedulingTestContext ctx, Func<Task> change)
    {
        var done = false;
        return InterceptingProxy<ISchedulingUnitOfWork>.Create(ctx.UnitOfWork, (method, _) =>
        {
            if (done || method.Name != nameof(ISchedulingUnitOfWork.ExecuteInTransactionAsync)) return;
            done = true;
            change().GetAwaiter().GetResult();
        });
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // ------------------------------------------------------------------ R1 service area deactivation

    [Fact]
    public async Task Deactivating_an_area_with_unfinished_appointments_is_refused_with_the_blocking_counts()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var area = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        Assert.Equal(area.Id, appointment.OperationalZoneId);

        var refused = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), area.Id, area.Name, false, null, null, area.Revision), None);

        Assert.Equal(SchedulingErrorCodes.ZoneInUse, refused.ErrorCode);
        Assert.Equal(409, SchedulingControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Contains("\"blockingAppointments\":1", Json(refused.Details));
        Assert.Contains("\"blockingInstallationProjects\":0", Json(refused.Details));
        Assert.True((await ctx.EnsureZoneAsync("Z-BINH-TAN")).IsActive);
        // Renaming while it stays active is not blocked.
        var renamed = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), area.Id, "Binh Tan (renamed)", true, null, null, area.Revision), None);
        Assert.True(renamed.Succeeded, renamed.Message);

        Assert.True((await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), None)).Succeeded);
        var deactivated = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), area.Id, "Binh Tan (renamed)", false, null, null, renamed.Value!.Revision), None);
        Assert.True(deactivated.Succeeded, deactivated.Message);
        Assert.False(deactivated.Value!.IsActive);
    }

    // ------------------------------------------------------------------ R5 background changes are retried

    [Fact]
    public async Task Customer_cancel_retries_when_the_appointment_only_changed_in_the_background()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var other = appointment.ReservedTechnicianUserId == a.Id ? b.Id : a.Id;
        // After the handler read it, the other technician accepts (revision changes, still changeable, same start).
        var u = BeforeFirstTransaction(ctx, async () => Assert.True((await ctx.AcceptAsync(other, appointment.Id)).Succeeded));

        var cancelled = await new Handlers(ctx, u).Cancel.HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, "Plans changed"), None);

        Assert.True(cancelled.Succeeded, cancelled.Message);
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Cancelled, stored.Status);
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(request.Request.Id)).Status);
        // The technician who accepted in between is told about the cancellation.
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == other && n.Type == AppointmentEventTypes.Cancelled);
    }

    [Fact]
    public async Task Customer_cancel_still_conflicts_when_the_appointment_was_cancelled_meanwhile()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("A", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        DateTimeOffset? at = ctx.Clock.Now;
        var u = BeforeFirstTransaction(ctx, () => ctx.Db.TechnicianAppointments.Where(x => x.Id == appointment.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, AppointmentStatuses.Cancelled).SetProperty(x => x.CancelledAt, at).SetProperty(x => x.Revision, x => x.Revision + 1)));

        var result = await new Handlers(ctx, u).Cancel.HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), None);

        Assert.Equal(SchedulingErrorCodes.ConcurrencyConflict, result.ErrorCode);
    }

    [Fact]
    public async Task Customer_reschedule_retries_when_a_technician_accepted_in_the_background_and_keeps_that_technician()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var u = BeforeFirstTransaction(ctx, async () => Assert.True((await ctx.AcceptAsync(a.Id, appointment.Id)).Succeeded));

        var result = await new Handlers(ctx, u).Reschedule.HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        var moved = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(At(14).ToUniversalTime(), moved.StartsAt);
        Assert.Equal(AppointmentStatuses.Accepted, moved.Status);
        Assert.Equal(a.Id, moved.AssignedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.Cancelled, (await ctx.AppointmentAsync(appointment.Id)).Status);
    }

    [Fact]
    public async Task Customer_reschedule_retries_when_the_hold_moved_in_the_background()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var other = appointment.ReservedTechnicianUserId == a.Id ? b.Id : a.Id;
        var u = BeforeFirstTransaction(ctx, () => ctx.Db.TechnicianAppointments.Where(x => x.Id == appointment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedTechnicianUserId, other).SetProperty(x => x.Revision, x => x.Revision + 1)));

        var result = await new Handlers(ctx, u).Reschedule.HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13), null), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        Assert.Equal(At(13).ToUniversalTime(), (await ctx.ActiveAppointmentAsync(request.Request.Id)).StartsAt);
    }

    // ------------------------------------------------------------------ R6 keep-technician path re-homes own holds

    /// <summary>T confirmed on 09:00 and holding a 14:00 pending hold; U serves the area too but has nothing that day.</summary>
    private static async Task<(SchedulingTestContext Ctx, Guid T, Guid U, SeededSurveyRequest Confirmed, Guid Accepted, Guid Hold, Guid Owner)> KeepScenarioAsync()
    {
        var ctx = Create();
        var t = await ctx.SeedTechnicianAsync("T", BinhTan);
        var holdRequest = await ctx.SeedSurveyRequestAsync(BinhTan, "Hold");
        var hold = await ctx.BookAsync(holdRequest, At(14));
        var confirmed = await ctx.SeedSurveyRequestAsync(BinhTan, "Confirmed");
        var accepted = await ctx.BookAsync(confirmed, At(9));
        Assert.Equal(t.Id, hold.ReservedTechnicianUserId);
        Assert.True((await ctx.AcceptAsync(t.Id, accepted.Id)).Succeeded);
        var u = await ctx.SeedTechnicianAsync("U", BinhTan);
        var owner = await ctx.ClaimAsync(confirmed);
        return (ctx, t.Id, u.Id, confirmed, accepted.Id, hold.Id, owner.Id);
    }

    [Fact]
    public async Task Customer_reschedule_keeps_the_technician_and_rehomes_their_own_conflicting_hold()
    {
        var (ctx, t, u, confirmed, _, hold, _) = await KeepScenarioAsync();
        await using var _ = ctx;

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(confirmed.Customer.Id, confirmed.Request.Id, At(14, 30), null), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        var moved = await ctx.ActiveAppointmentAsync(confirmed.Request.Id);
        Assert.Equal(AppointmentStatuses.Accepted, moved.Status);                        // not PENDING_ACCEPTANCE
        Assert.Equal(t, moved.AssignedTechnicianUserId);
        Assert.Equal(AcceptanceStatuses.Confirmed, result.Value!.AcceptanceStatus);
        var rehomed = await ctx.AppointmentAsync(hold);
        Assert.Equal(u, rehomed.ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, rehomed.Status);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.ToListAsync(), e => e.EventType == AppointmentEventTypes.HoldMoved && e.AppointmentId == hold);
        Assert.Equal(SurveyRequestStatus.Scheduled, (await ctx.RequestAsync(confirmed.Request.Id)).Status);
    }

    [Fact]
    public async Task Sales_reschedule_naming_the_confirmed_technician_rehomes_their_hold_and_records_the_assignment()
    {
        var (ctx, t, u, confirmed, _, hold, owner) = await KeepScenarioAsync();
        await using var _ = ctx;

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner, confirmed.Request.Id, At(14, 30), "Customer asked", t), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        Assert.Equal(AppointmentStatuses.Assigned, result.Value!.AppointmentStatus);
        Assert.Equal(t, result.Value.TechnicianUserId);
        Assert.Equal(u, (await ctx.AppointmentAsync(hold)).ReservedTechnicianUserId);
        var events = await ctx.Db.TechnicianAppointmentEvents.Where(e => e.AppointmentId == result.Value.AppointmentId).ToListAsync();
        Assert.Contains(events, e => e.EventType == AppointmentEventTypes.Rescheduled);
        var assigned = Assert.Single(events, e => e.EventType == AppointmentEventTypes.Assigned);
        Assert.Equal(t, assigned.NewTechnicianUserId);
        Assert.DoesNotContain(events, e => e.EventType == AppointmentEventTypes.Reassigned);
    }

    [Fact]
    public async Task Sales_reschedule_naming_another_technician_records_the_reassignment()
    {
        var (ctx, t, u, confirmed, _, _, owner) = await KeepScenarioAsync();
        await using var _ = ctx;

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner, confirmed.Request.Id, At(10), "T is late", u), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        var reassigned = await ctx.Db.TechnicianAppointmentEvents.SingleAsync(e => e.AppointmentId == result.Value!.AppointmentId && e.EventType == AppointmentEventTypes.Reassigned);
        Assert.Equal(t, reassigned.PreviousTechnicianUserId);
        Assert.Equal(u, reassigned.NewTechnicianUserId);
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == t && n.Type == AppointmentEventTypes.Reassigned);
    }

    // ------------------------------------------------------------------ R7 out-of-area acceptance discloses nothing

    [Fact]
    public async Task Out_of_area_acceptance_is_not_found_whatever_the_state_of_the_appointment()
    {
        await using var ctx = Create();
        var insider = await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var outsider = await ctx.SeedTechnicianAsync("District 7 tech", District7);
        var accepted = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Accepted"), At(9));
        Assert.True((await ctx.AcceptAsync(insider.Id, accepted.Id)).Succeeded);
        var cancelledRequest = await ctx.SeedSurveyRequestAsync(BinhTan, "Cancelled");
        var cancelled = await ctx.BookAsync(cancelledRequest, At(13));
        Assert.True((await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(cancelledRequest.Customer.Id, cancelledRequest.Request.Id, null), None)).Succeeded);
        var pending = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Pending"), At(15));

        foreach (var id in new[] { accepted.Id, cancelled.Id, pending.Id, Guid.NewGuid() })
        {
            Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.AcceptAsync(outsider.Id, id)).ErrorCode);
            Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.AcceptAsync(outsider.Id, id, expectedRevision: 99)).ErrorCode);
            Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetOpenAsync(outsider.Id, id, None)).ErrorCode);
        }
        // In-area technicians still get the precise state.
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, (await ctx.AcceptAsync(insider.Id, cancelled.Id)).ErrorCode);
        Assert.Equal(insider.Id, (await ctx.AppointmentAsync(accepted.Id)).AssignedTechnicianUserId);
    }

    // ------------------------------------------------------------------ R8 admin changes re-read the holds, lowered daily cap

    [Fact]
    public async Task Leave_added_while_a_new_hold_lands_retries_and_rehomes_it_to_a_locked_candidate()
    {
        await using var ctx = Create();
        var first = await ctx.SeedTechnicianAsync("First", BinhTan);
        var second = await ctx.SeedTechnicianAsync("Second", BinhTan);
        var morning = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Morning"), At(9));
        var busy = morning.ReservedTechnicianUserId!.Value;
        var x = busy == first.Id ? second.Id : first.Id;          // no commitment yet: takes the next booking
        var late = await ctx.SeedSurveyRequestAsync(BinhTan, "Late");
        Guid? lateId = null;
        // The booking commits after the admin handler read X's (empty) holds and before its transaction.
        var u = BeforeFirstTransaction(ctx, async () => lateId = (await ctx.BookAsync(late, At(14))).Id);

        var leave = await new Handlers(ctx, u).Admin.AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), x, At(13), At(17), "Errand"), None);

        Assert.True(leave.Succeeded, leave.Message);
        var hold = await ctx.AppointmentAsync(lateId!.Value);
        Assert.Equal(busy, hold.ReservedTechnicianUserId);                               // re-homed, not escalated without a holder
        Assert.Equal(AppointmentStatuses.PendingAcceptance, hold.Status);
    }

    [Fact]
    public async Task Lowering_the_daily_limit_rehomes_the_latest_holds_above_the_new_cap()
    {
        await using var ctx = Create();
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var r1 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "One"), At(9));
        var r2 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Two"), At(13));
        var r3 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Three"), At(15));
        Assert.All(new[] { r1, r2, r3 }, r => Assert.Equal(x.Id, r.ReservedTechnicianUserId));
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);

        var lowered = await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(x.Id, true, 1), None);

        Assert.True(lowered.Succeeded, lowered.Message);
        Assert.Equal(1, lowered.Value!.MaxAppointmentsPerDay);
        Assert.Equal(x.Id, (await ctx.AppointmentAsync(r1.Id)).ReservedTechnicianUserId);
        Assert.Equal(y.Id, (await ctx.AppointmentAsync(r2.Id)).ReservedTechnicianUserId);
        Assert.Equal(y.Id, (await ctx.AppointmentAsync(r3.Id)).ReservedTechnicianUserId);
        Assert.Equal(1, await ctx.Db.TechnicianAppointments.CountAsync(a => a.ReservedTechnicianUserId == x.Id && a.Status != AppointmentStatuses.Cancelled));
    }

    [Fact]
    public async Task Lowering_the_daily_limit_below_the_confirmed_work_of_a_day_is_refused()
    {
        await using var ctx = Create();
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var r1 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "One"), At(9));
        var r2 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Two"), At(13));
        Assert.True((await ctx.AcceptAsync(x.Id, r1.Id)).Succeeded);
        Assert.True((await ctx.AcceptAsync(x.Id, r2.Id)).Succeeded);

        var refused = await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(x.Id, true, 1), None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, refused.ErrorCode);
        Assert.Contains(EligibilityReasons.DailyLimitReached, Json(refused.Details));
        Assert.Null((await ctx.Db.TechnicianProfiles.AsNoTracking().SingleAsync(p => p.UserId == x.Id)).MaxAppointmentsPerDay);
    }

    // ------------------------------------------------------------------ R9 locked/conditional Sales owner change

    /// <summary>Right before the conditional owner UPDATE runs, another Sales user takes the request (as an unlocked concurrent claim would).</summary>
    private sealed class OwnerChangeInjector(string otherSalesEmail) : DbCommandInterceptor
    {
        public bool Injected { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("survey_request")
                && command.CommandText.Contains("assigned_sale_id"))
            {
                Injected = true;
                using var inject = command.Connection!.CreateCommand();
                inject.Transaction = command.Transaction;
                inject.CommandText = $"UPDATE survey_request SET assigned_sale_id = (SELECT id FROM user_account WHERE email = '{otherSalesEmail}')";
                inject.ExecuteNonQuery();
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task The_owner_update_is_conditional_on_the_owner_it_read()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var first = await ctx.ClaimAsync(request);
        var sneaky = await ctx.SeedSalesAsync("Sneaky");
        var target = await ctx.SeedSalesAsync("Target");
        var injector = new OwnerChangeInjector(sneaky.Email);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Db.Database.GetDbConnection())
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .AddInterceptors(injector)
            .Options);
        var uow = new SchedulingUnitOfWork(db);

        var update = await uow.ExecuteInTransactionAsync<(SalesOwnerUpdate, Guid?)>(async token =>
        {
            var u = await uow.SetSalesOwnerAsync(request.Request.Id, target.Id, DateTimeOffset.UtcNow, token);
            var owner = await db.SurveyRequests.AsNoTracking().Where(r => r.Id == request.Request.Id).Select(r => r.AssignedSaleId).SingleAsync(token);
            return ((u, owner), false);
        }, None);

        Assert.True(injector.Injected);
        Assert.True(update.Item1.Found);
        Assert.False(update.Item1.Updated);                     // the concurrent owner was not overwritten
        Assert.Equal(first.Id, update.Item1.PreviousSaleId);
        Assert.Equal(sneaky.Id, update.Item2);
    }

    [Fact]
    public async Task A_lost_owner_race_is_re_read_and_the_audit_names_the_true_previous_owner_or_ends_unavailable()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var first = await ctx.ClaimAsync(request);
        var second = await ctx.SeedSalesAsync("Second");
        var manager = await ctx.Inner.SeedUserAsync("Manager");
        var losses = 1;
        var u = InterceptingProxy<ISchedulingUnitOfWork>.Create(ctx.UnitOfWork, replace: (method, _) =>
            method.Name == nameof(ISchedulingUnitOfWork.SetSalesOwnerAsync) && losses-- > 0 ? Task.FromResult(new SalesOwnerUpdate(true, false, null)) : null);

        var moved = await new Handlers(ctx, u).SalesOwner.HandleAsync(new SetSalesOwnerCommand(manager.Id, request.Request.Id, second.Id, "Holiday"), None);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(first.Id, moved.Value!.PreviousSaleUserId);
        Assert.Equal(first.Id, (await ctx.Db.SurveyRequestEvents.SingleAsync()).PreviousSaleUserId);

        losses = int.MaxValue;
        var third = await ctx.SeedSalesAsync("Third");
        var unavailable = await new Handlers(ctx, u).SalesOwner.HandleAsync(new SetSalesOwnerCommand(manager.Id, request.Request.Id, third.Id, "Again"), None);
        Assert.Equal(PreSurveyErrorCodes.SurveyRequestUnavailable, unavailable.ErrorCode);
        Assert.Equal(409, SchedulingControllerBase.StatusFor(unavailable.ErrorCode!));
        Assert.Equal(second.Id, (await ctx.RequestAsync(request.Request.Id)).AssignedSaleId);
        Assert.Single(await ctx.Db.SurveyRequestEvents.ToListAsync());
    }

    // ------------------------------------------------------------------ R10 release recomputes the acceptance deadline

    [Fact]
    public async Task Release_starts_a_fresh_acceptance_window_capped_by_the_minimum_lead()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));                   // booked Mon 06:00: deadline Mon 10:00
        Assert.True((await ctx.AcceptAsync(a.Id, appointment.Id)).Succeeded);

        // Mon 11:00: the booking deadline passed, but the release opens a new window until LEAST(15:00 Mon, 07:00 Tue).
        ctx.Clock.Now = DefaultNow.AddHours(5).ToUniversalTime();
        Assert.True((await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(a.Id, appointment.Id, "Sick"), None)).Succeeded);
        var reopened = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, reopened.Status);
        Assert.Equal(DefaultNow.AddHours(9).ToUniversalTime(), reopened.AcceptanceDeadlineAt);
        Assert.Null(reopened.EscalatedAt);
    }

    [Fact]
    public async Task Release_inside_the_minimum_lead_escalates_even_with_a_new_holder()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(a.Id, appointment.Id)).Succeeded);

        // A deployment whose on-site survey may start only 60 minutes early: releasing 90 minutes before is still allowed.
        ctx.OnSiteOptions.EarlyStartMinutes = 60;
        ctx.Clock.Now = At(7, 30).ToUniversalTime();                            // 90 minutes before the start, lead 120
        Assert.True((await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(a.Id, appointment.Id, "Sick"), None)).Succeeded);

        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Escalated, stored.Status);
        Assert.Equal(b.Id, stored.ReservedTechnicianUserId);
        Assert.Equal(At(7).ToUniversalTime(), stored.AcceptanceDeadlineAt);
        Assert.NotNull(stored.EscalatedAt);
    }

    // ------------------------------------------------------------------ restored coverage

    [Fact]
    public async Task A_custom_weekly_schedule_drives_slots_and_the_holder_choice()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Day shift", BinhTan);
        var evening = await ctx.SeedTechnicianAsync("Evening", BinhTan);
        var set = await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(evening.Id, false,
            [new WorkingPeriodInput(DayOfWeek.Tuesday, new TimeOnly(17, 0), new TimeOnly(20, 0))]), None);
        Assert.True(set.Succeeded, set.Message);
        Assert.False(set.Value!.UsesDefault);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var tuesday = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), None)).Value!;
        var evenings = tuesday.Slots.Where(s => s.StartsAt >= At(17)).Select(s => s.StartsAt).ToList();
        Assert.Equal([At(17), At(17, 30), At(18), At(18, 30)], evenings);
        Assert.All(tuesday.Slots.Where(s => s.StartsAt >= At(17)), s => Assert.Equal(1, s.KnownAvailableTechnicians));
        // On Wednesday the evening technician has a day off: no evening slots.
        var wednesday = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day.AddDays(1)), None)).Value!;
        Assert.DoesNotContain(wednesday.Slots, s => s.StartsAt.TimeOfDay >= TimeSpan.FromHours(17));

        var booked = await ctx.BookAsync(request, At(17, 30));
        Assert.Equal(evening.Id, booked.ReservedTechnicianUserId);
    }

    [Fact]
    public void The_working_schedule_validator_rejects_overlaps_empty_periods_and_too_many_periods()
    {
        var validator = new SetWorkingScheduleCommandValidator();
        static WorkingPeriodInput P(DayOfWeek d, int from, int to) => new(d, new TimeOnly(from, 0), new TimeOnly(to, 0));
        var tech = Guid.NewGuid();

        Assert.True(validator.Validate(new SetWorkingScheduleCommand(tech, false, [P(DayOfWeek.Monday, 8, 12), P(DayOfWeek.Monday, 13, 17)])).IsValid);
        Assert.True(validator.Validate(new SetWorkingScheduleCommand(tech, false, [P(DayOfWeek.Monday, 8, 12), P(DayOfWeek.Tuesday, 10, 14)])).IsValid);

        var overlap = validator.Validate(new SetWorkingScheduleCommand(tech, false, [P(DayOfWeek.Monday, 8, 12), P(DayOfWeek.Monday, 11, 15)]));
        Assert.False(overlap.IsValid);
        Assert.Contains(overlap.Errors, e => e.ErrorMessage.Contains("overlap"));

        Assert.False(validator.Validate(new SetWorkingScheduleCommand(tech, false, [P(DayOfWeek.Monday, 12, 12)])).IsValid);
        Assert.False(validator.Validate(new SetWorkingScheduleCommand(tech, false, [P(DayOfWeek.Monday, 12, 9)])).IsValid);

        var many = Enumerable.Range(0, 43).Select(i => new WorkingPeriodInput((DayOfWeek)(i % 7), new TimeOnly(i / 7, 0), new TimeOnly(i / 7, 30))).ToList();
        var tooMany = validator.Validate(new SetWorkingScheduleCommand(tech, false, many));
        Assert.False(tooMany.IsValid);
        Assert.Contains(tooMany.Errors, e => e.ErrorMessage.Contains("42"));
        Assert.True(validator.Validate(new SetWorkingScheduleCommand(tech, false, many.Take(42).ToList())).IsValid);

        Assert.False(validator.Validate(new SetWorkingScheduleCommand(tech, false, [])).IsValid);
        Assert.True(validator.Validate(new SetWorkingScheduleCommand(tech, true, [])).IsValid);
    }

    [Fact]
    public async Task Scheduling_working_days_decide_which_days_are_bookable()
    {
        await using var ctx = Create();
        ctx.Reconfigure(o => o.WorkingDays = "MON,TUE,WED,THU,FRI");
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var saturday = Day.AddDays(4);

        Assert.NotEmpty((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), None)).Value!.Slots);
        Assert.Empty((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, saturday), None)).Value!.Slots);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime,
            (await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9, 0, saturday), null), None)).ErrorCode);

        ctx.Reconfigure(o => o.WorkingDays = "MON,TUE,WED,THU,FRI,SAT");
        Assert.NotEmpty((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, saturday), None)).Value!.Slots);
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9, 0, saturday), null), None)).Succeeded);
    }

    [Fact]
    public async Task A_slot_blocked_by_the_daily_limit_becomes_bookable_after_a_cancellation()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("One a day", BinhTan, maxPerDay: 1);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        await ctx.BookAsync(first, At(9));

        Assert.Empty((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(second.Customer.Id, second.Site.Id, Day), None)).Value!.Slots);
        // No capacity: the precise code (the member is free on other days of the horizon) and a dispatch is available.
        var refused = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(second.Customer.Id, second.Request.Id, At(13), null), None);
        Assert.Equal(SchedulingErrorCodes.ScheduleConflict, refused.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(SchedulingErrorCodes.ScheduleConflict, true), refused.Details);

        Assert.True((await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, null), None)).Succeeded);

        Assert.Contains((await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(second.Customer.Id, second.Site.Id, Day), None)).Value!.Slots, s => s.StartsAt == At(13));
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(second.Customer.Id, second.Request.Id, At(13), null), None)).Succeeded);
    }

    [Fact]
    public async Task A_failed_sales_reschedule_leaves_the_original_appointment_untouched()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var onLeave = await ctx.SeedTechnicianAsync("On leave", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, original.Id)).Succeeded);
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), onLeave.Id, At(13), At(17), "Leave"), None)).Succeeded);
        var owner = await ctx.ClaimAsync(request);
        var before = await ctx.AppointmentAsync(original.Id);
        var eventsBefore = await ctx.Db.TechnicianAppointmentEvents.CountAsync();

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner.Id, request.Request.Id, At(14), "Afternoon", onLeave.Id), None);

        Assert.Equal(SchedulingErrorCodes.TechnicianOnLeave, result.ErrorCode);
        var after = await ctx.AppointmentAsync(original.Id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(AppointmentStatuses.Accepted, after.Status);
        Assert.Equal(before.StartsAt, after.StartsAt);
        Assert.Null(after.CancelledAt);
        Assert.Null(after.ReplacedByAppointmentId);
        Assert.Equal(tech.Id, after.AssignedTechnicianUserId);
        Assert.Equal(1, await ctx.Db.TechnicianAppointments.CountAsync(a => a.SurveyRequestId == request.Request.Id));
        Assert.Equal(eventsBefore, await ctx.Db.TechnicianAppointmentEvents.CountAsync());
        var surveyRequest = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Scheduled, surveyRequest.Status);
        Assert.Equal(before.StartsAt, surveyRequest.ScheduledAt);
    }

    [Fact]
    public async Task Staff_assignment_of_an_unknown_or_inactive_technician_is_rejected_and_the_assignment_kept()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var suspended = await ctx.SeedTechnicianAsync("Suspended", BinhTan);
        var disabled = await ctx.SeedTechnicianAsync("Disabled", BinhTan, active: false);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);
        await ctx.Db.UserAccounts.Where(u => u.Id == suspended.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Suspended));
        var owner = await ctx.ClaimAsync(request);
        var manager = await ctx.Inner.SeedUserAsync("Manager");
        var before = await ctx.AppointmentAsync(appointment.Id);

        Assert.Equal(SchedulingErrorCodes.TechnicianNotFound,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, Guid.NewGuid(), "Swap"), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianInactive,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, suspended.Id, "Swap"), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianSchedulingDisabled,
            (await ctx.Assign().OverrideAsync(new OverrideTechnicianCommand(manager.Id, appointment.Id, disabled.Id, "Swap"), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianNotFound,
            (await ctx.Assign().OverrideAsync(new OverrideTechnicianCommand(manager.Id, appointment.Id, manager.Id, "Not a technician"), None)).ErrorCode);

        var after = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(tech.Id, after.AssignedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.Accepted, after.Status);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task Only_the_accepted_technician_reads_the_customer_contact_and_street()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var bystander = await ctx.SeedTechnicianAsync("Bystander", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var holder = appointment.ReservedTechnicianUserId!.Value;
        var acceptor = holder == a.Id ? b.Id : a.Id;
        // While open, nobody (not even the capacity holder) sees contact data.
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetAsync(holder, appointment.Id, None)).ErrorCode);

        Assert.True((await ctx.AcceptAsync(acceptor, appointment.Id)).Succeeded);

        foreach (var outsider in new[] { holder, bystander.Id })
        {
            var refused = await ctx.Jobs().GetAsync(outsider, appointment.Id, None);
            Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, refused.ErrorCode);
            Assert.Equal(404, SchedulingControllerBase.StatusFor(refused.ErrorCode!));
            Assert.Null(refused.Value);
            Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetOpenAsync(outsider, appointment.Id, None)).ErrorCode);
            Assert.DoesNotContain(await ctx.Jobs().ListAsync(outsider, None), j => j.AppointmentId == appointment.Id);
        }

        var job = await ctx.Jobs().GetAsync(acceptor, appointment.Id, None);
        Assert.True(job.Succeeded, job.Message);
        Assert.Equal(request.Customer.Email, job.Value!.Customer.Email);
        Assert.Equal("1 Le Loi", job.Value.Property.StreetLine);
    }
}
