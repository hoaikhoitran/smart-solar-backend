using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSolar.Api.Background;
using SmartSolar.Api.Controllers;
using SmartSolar.Api.Extensions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// The appointment sweep and scheduling edge cases: open appointments nobody accepted before their start are auto-cancelled,
/// the escalation/cancel sweep survives per-appointment failures and never escalates started appointments, legacy bookings
/// without a zone, the Sales ownership re-check inside the assignment transaction, and the escalated queue / open-appointment
/// list rate limit and constant query count.
/// Geography is SYNTHETIC.
/// </summary>
public sealed class AppointmentSweepAndEdgeCaseTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The real background worker over a test unit of work (each scope gets a fresh unit of work).</summary>
    internal static AppointmentEscalationWorker Worker(Func<ISchedulingUnitOfWork> unitOfWork, TimeProvider clock, ILoggerProvider? logs = null,
        SmartSolar.Modules.SystemSettings.ISchedulingPolicyProvider? policies = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs is not null) b.AddProvider(logs);
        });
        services.AddSingleton(clock);
        services.AddScoped(_ => unitOfWork());
        // Bootstrap defaults (acceptance reminders disabled) unless the test passes the context's settings provider.
        services.AddSingleton(policies ?? new FixedPolicyProvider(SmartSolar.Modules.SystemSettings.SchedulingPolicyDefinitions.Default));
        services.AddScoped<AppointmentEscalationService>();
        var provider = services.BuildServiceProvider();
        return new AppointmentEscalationWorker(provider.GetRequiredService<IServiceScopeFactory>(), new SchedulingOptions(),
            provider.GetRequiredService<ILogger<AppointmentEscalationWorker>>());
    }

    /// <summary>A pre-redesign appointment: open, held by <paramref name="holder"/>, no zone recorded.</summary>
    private static async Task<TechnicianAppointment> SeedLegacyAppointmentAsync(SchedulingTestContext ctx, SeededSurveyRequest r, Guid holder, DateTimeOffset start)
    {
        var now = ctx.Clock.GetUtcNow();
        var appointment = new TechnicianAppointment
        {
            Id = Guid.NewGuid(), AppointmentType = AppointmentTypes.PreSurvey, SurveyRequestId = r.Request.Id, PropertySiteId = r.Site.Id,
            OperationalZoneId = null, StartsAt = start.ToUniversalTime(), EndsAt = start.ToUniversalTime().AddMinutes(90), ReservedTechnicianUserId = holder,
            Status = AppointmentStatuses.PendingAcceptance, AcceptanceDeadlineAt = start.ToUniversalTime().AddHours(-2),
            BookedByUserId = r.Customer.Id, BookedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.Db.TechnicianAppointments.Add(appointment);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return appointment;
    }

    // ------------------------------------------------------------------ H1 auto-cancel of open appointments that started

    [Fact]
    public async Task The_worker_cancels_an_open_appointment_nobody_accepted_before_its_start_and_the_customer_can_book_again()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);
        var worker = Worker(() => ctx.UnitOfWork, ctx.Clock);
        ctx.Clock.Now = At(9, 5).ToUniversalTime();

        await worker.RunOnceAsync(None);
        await worker.RunOnceAsync(None);                                    // idempotent: nothing left to do

        var cancelled = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Cancelled, cancelled.Status);
        Assert.Equal(CancellationReasons.NotAcceptedBeforeStart, cancelled.CancellationReason);
        Assert.Equal(ctx.Clock.Now, cancelled.CancelledAt);
        var events = await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.AppointmentId == appointment.Id).ToListAsync();
        var cancelEvent = Assert.Single(events, e => e.EventType == AppointmentEventTypes.Cancelled);
        Assert.Equal(CancellationReasons.NotAcceptedBeforeStart, cancelEvent.Reason);
        Assert.Null(cancelEvent.ActorUserId);
        Assert.DoesNotContain(events, e => e.EventType == AppointmentEventTypes.Escalated);     // started: cancelled, never escalated
        var notification = Assert.Single(await ctx.Db.SchedulingNotifications.AsNoTracking().Where(n => n.AppointmentId == appointment.Id).ToListAsync());
        Assert.Equal(owner.Id, notification.RecipientUserId);
        Assert.Equal(AppointmentEventTypes.Cancelled, notification.Type);
        var stored = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Pending, stored.Status);
        Assert.Null(stored.ScheduledAt);

        var rebooked = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9, date: Day.AddDays(1)), null), None);
        Assert.True(rebooked.Succeeded, rebooked.Message);
        Assert.True(rebooked.Value!.Created);
    }

    [Fact]
    public async Task Before_the_worker_runs_a_started_open_appointment_shows_as_cancelled_and_booking_again_cancels_it_first()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var owner = await ctx.ClaimAsync(request);
        var appointment = await ctx.BookAsync(request, At(9));
        ctx.Clock.Now = At(9, 5).ToUniversalTime();

        var view = Assert.Single(await ctx.CustomerQueries().ListAsync(request.Customer.Id, None)).Appointment!;
        Assert.Equal(AcceptanceStatuses.Cancelled, view.AcceptanceStatus);
        Assert.False(view.CanCustomerChange);
        Assert.Empty(await ctx.Escalated().HandleAsync(owner.Id, None));
        Assert.Empty(await ctx.Jobs().ListOpenAsync(tech.Id, None));

        var rebooked = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10, date: Day.AddDays(1)), null), None);

        Assert.True(rebooked.Succeeded, rebooked.Message);
        var old = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Cancelled, old.Status);
        Assert.Equal(CancellationReasons.NotAcceptedBeforeStart, old.CancellationReason);
        Assert.Equal(rebooked.Value!.AppointmentId, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Id);
    }

    [Fact]
    public async Task A_started_open_appointment_no_longer_blocks_deactivating_its_zone()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        await ctx.BookAsync(request, At(9));
        var area = await ctx.EnsureZoneAsync(BinhTan);
        ctx.Clock.Now = At(9, 5).ToUniversalTime();

        var deactivated = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), area.Id, area.Name, false, null, null, area.Revision), None);

        Assert.True(deactivated.Succeeded, deactivated.Message);
    }

    // ------------------------------------------------------------------ L4 worker robustness

    [Fact]
    public async Task Escalation_skips_appointments_that_already_started()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        ctx.Clock.Now = At(9, 5).ToUniversalTime();                        // deadline and start both passed

        Assert.Equal(0, await ctx.Escalation().EscalateDueAsync(10, None));
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(appointment.Id)).Status);
        Assert.Empty(await ctx.Db.SchedulingNotifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task One_failing_appointment_does_not_abort_the_sweep_and_only_its_id_is_logged()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("X", BinhTan);
        await ctx.SeedTechnicianAsync("Y", BinhTan);
        var first = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "First"), At(9));
        var second = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Second"), At(9));
        var later = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Later"), At(15));
        var poisoned = first.Id;
        var poisonedDue = later.Id;
        var logs = new CapturingLoggerProvider();
        ISchedulingUnitOfWork Proxied() => InterceptingProxy<ISchedulingUnitOfWork>.Create(ctx.UnitOfWork, (method, args) =>
        {
            if (method.Name is nameof(ISchedulingUnitOfWork.TryCancelNotAcceptedAsync) or nameof(ISchedulingUnitOfWork.TryEscalateAsync)
                && args[0] is Guid id && (id == poisoned || id == poisonedDue))
                throw new InvalidOperationException("customer phone 0901234567");
        });
        // The 9:00 appointments started; the 15:00 one is only past its acceptance deadline (booking + 4 h).
        ctx.Clock.Now = At(13, 30).ToUniversalTime();

        var processed = await Worker(Proxied, ctx.Clock, logs).RunOnceAsync(None);

        Assert.Equal(1, processed);
        Assert.Equal(AppointmentStatuses.Cancelled, (await ctx.AppointmentAsync(second.Id)).Status);
        Assert.NotEqual(AppointmentStatuses.Cancelled, (await ctx.AppointmentAsync(first.Id)).Status);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(later.Id)).Status);
        Assert.Contains(logs.Messages, m => m.Contains(first.Id.ToString()));
        Assert.Contains(logs.Messages, m => m.Contains(later.Id.ToString()));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("0901234567"));

        poisoned = poisonedDue = Guid.Empty;                                // the next tick retries them
        Assert.Equal(2, await Worker(Proxied, ctx.Clock, logs).RunOnceAsync(None));
        Assert.Equal(AppointmentStatuses.Cancelled, (await ctx.AppointmentAsync(first.Id)).Status);
        Assert.Equal(AppointmentStatuses.Escalated, (await ctx.AppointmentAsync(later.Id)).Status);
    }

    // ------------------------------------------------------------------ M1 legacy appointments without a zone

    [Fact]
    public async Task A_site_whose_active_appointments_have_no_zone_can_set_its_location_and_they_get_the_new_zone()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, withCodes: false);
        var legacy = await SeedLegacyAppointmentAsync(ctx, request, tech.Id, At(9));
        var district = await ctx.EnsureZoneAsync(BinhTan);

        var set = await ctx.Zones().SetSiteLocationAsync(new SetSiteLocationCommand(request.Customer.Id, request.Site.Id, Province, CommuneCodeOf(BinhTan)), None);

        Assert.True(set.Succeeded, set.Message);
        Assert.Equal(district.Id, set.Value!.Zone!.ZoneId);
        var updated = await ctx.AppointmentAsync(legacy.Id);
        Assert.Equal(district.Id, updated.OperationalZoneId);
        Assert.Equal(legacy.Revision + 1, updated.Revision);
        Assert.True((await ctx.AcceptAsync(tech.Id, legacy.Id)).Succeeded);

        // The appointment now records a zone: further location changes are refused as before.
        Assert.Equal(SchedulingErrorCodes.SiteHasActiveBooking, (await ctx.Zones().SetSiteLocationAsync(
            new SetSiteLocationCommand(request.Customer.Id, request.Site.Id, Province, CommuneCodeOf(District7)), None)).ErrorCode);
    }

    [Fact]
    public async Task Without_a_site_location_accept_assign_and_reschedule_answer_site_location_required()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, withCodes: false);
        var owner = await ctx.ClaimAsync(request);
        var legacy = await SeedLegacyAppointmentAsync(ctx, request, tech.Id, At(9));

        var accepted = await ctx.AcceptAsync(tech.Id, legacy.Id);
        var assigned = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, tech.Id, "Fallback"), None);
        var overridden = await ctx.Assign().OverrideAsync(new OverrideTechnicianCommand(owner.Id, legacy.Id, tech.Id, "Override"), None);
        var rescheduled = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner.Id, request.Request.Id, At(10), "Customer asked", null), None);

        Assert.All(new[] { accepted.ErrorCode, assigned.ErrorCode, overridden.ErrorCode, rescheduled.ErrorCode },
            code => Assert.Equal(SchedulingErrorCodes.SiteLocationRequired, code));
        Assert.Equal(422, SchedulingControllerBase.StatusFor(SchedulingErrorCodes.SiteLocationRequired));
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(legacy.Id)).Status);
    }

    [Fact]
    public async Task Self_acceptance_outside_the_area_of_a_coded_site_stays_not_found()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var outsider = await ctx.SeedTechnicianAsync("Outsider", District7);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.AcceptAsync(outsider.Id, appointment.Id)).ErrorCode);
    }

    // ------------------------------------------------------------------ L2 Sales ownership re-checked inside the transaction

    /// <summary>Runs <paramref name="change"/> once, right before the handler's first transaction starts (after its pre-checks).</summary>
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

    [Fact]
    public async Task An_owner_change_between_the_check_and_the_assignment_transaction_is_refused()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);
        var other = await ctx.SeedSalesAsync("Other");
        var changed = false;
        var u = BeforeFirstTransaction(ctx, async () =>
        {
            changed = true;
            await ctx.Db.SurveyRequests.Where(r => r.Id == request.Request.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.AssignedSaleId, other.Id));
        });
        var eligibility = new TechnicianEligibility(u, ctx.Calendar, ctx.Options, ctx.Policies);
        var handler = new AssignTechnicianHandler(u, new AppointmentAssignmentService(u, eligibility, ctx.Options, ctx.Clock, ctx.DispatchStore), ctx.Calendar);

        var result = await handler.HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, tech.Id, "Fallback"), None);

        Assert.True(changed);
        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotOwned, result.ErrorCode);
        Assert.Equal(403, SchedulingControllerBase.StatusFor(result.ErrorCode!));
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, stored.Status);
        Assert.Equal(appointment.Revision, stored.Revision);
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(request.Request.Id)).Status);
    }

    // ------------------------------------------------------------------ L3 rate limit and constant query count

    [Fact]
    public void The_escalated_queue_uses_the_scheduling_read_policy()
    {
        var action = typeof(SurveyRequestsController).GetMethod(nameof(SurveyRequestsController.GetEscalated))!;
        Assert.Equal(RateLimitingExtensions.SchedulingReadPolicy, action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>A unit of work on the test connection whose database commands are counted.</summary>
    private static (SchedulingUnitOfWork UnitOfWork, CommandCounter Counter) Counted(SchedulingTestContext ctx)
    {
        var counter = new CommandCounter();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Db.Database.GetDbConnection())
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .AddInterceptors(counter)
            .Options);
        return (new SchedulingUnitOfWork(db), counter);
    }

    /// <summary>Books one open appointment on each of the next <paramref name="count"/> days (different sites and dates).</summary>
    private static async Task<List<TechnicianAppointment>> BookAcrossDaysAsync(SchedulingTestContext ctx, int first, int count)
    {
        var booked = new List<TechnicianAppointment>();
        for (var i = first; i < first + count; i++)
            booked.Add(await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(i % 2 == 0 ? BinhTan : District7, $"Customer {i}"), At(9, date: Day.AddDays(i))));
        return booked;
    }

    [Fact]
    public async Task The_escalated_queue_runs_a_constant_number_of_queries()
    {
        await using var ctx = Create();
        var sales = await ctx.SeedSalesAsync();
        await ctx.SeedTechnicianInZonesAsync("Both", [ZoneCodeOf(BinhTan), ZoneCodeOf(District7)]);
        await ctx.SeedTechnicianAsync("Binh Tan only", BinhTan);
        await BookAcrossDaysAsync(ctx, 0, 1);
        await ctx.Db.TechnicianAppointments.ExecuteUpdateAsync(s => s.SetProperty(a => a.AcceptanceDeadlineAt, ctx.Clock.Now.AddMinutes(-1)));
        var (u, counter) = Counted(ctx);
        var handler = new EscalatedAppointmentsHandler(u, new TechnicianEligibility(u, ctx.Calendar, ctx.Options, ctx.Policies), ctx.Calendar, ctx.Clock);

        counter.Count = 0;
        var one = await handler.HandleAsync(sales.Id, None);
        var oneRow = counter.Count;

        await BookAcrossDaysAsync(ctx, 1, 4);
        await ctx.Db.TechnicianAppointments.ExecuteUpdateAsync(s => s.SetProperty(a => a.AcceptanceDeadlineAt, ctx.Clock.Now.AddMinutes(-1)));
        counter.Count = 0;
        var five = await handler.HandleAsync(sales.Id, None);

        Assert.Single(one);
        Assert.Equal(5, five.Count);
        Assert.All(five, v => Assert.True(v.HasEligibleTechnician));
        Assert.Equal(oneRow, counter.Count);
    }

    [Fact]
    public async Task The_technician_open_appointment_list_runs_a_constant_number_of_queries()
    {
        await using var ctx = Create();
        var both = await ctx.SeedTechnicianInZonesAsync("Both", [ZoneCodeOf(BinhTan), ZoneCodeOf(District7)]);
        await ctx.SeedTechnicianAsync("Binh Tan only", BinhTan);
        await BookAcrossDaysAsync(ctx, 0, 1);
        var (u, counter) = Counted(ctx);
        var eligibility = new TechnicianEligibility(u, ctx.Calendar, ctx.Options, ctx.Policies);
        var jobs = new TechnicianJobHandlers(u, new AppointmentAssignmentService(u, eligibility, ctx.Options, ctx.Clock, ctx.DispatchStore), eligibility, ctx.Calendar, ctx.Clock);

        counter.Count = 0;
        var one = await jobs.ListOpenAsync(both.Id, None);
        var oneRow = counter.Count;

        await BookAcrossDaysAsync(ctx, 1, 4);
        counter.Count = 0;
        var five = await jobs.ListOpenAsync(both.Id, None);

        Assert.Single(one);
        Assert.Equal(5, five.Count);
        Assert.All(five, v => Assert.True(v.CanAccept, v.Reason));
        Assert.Equal(oneRow, counter.Count);
    }
}
