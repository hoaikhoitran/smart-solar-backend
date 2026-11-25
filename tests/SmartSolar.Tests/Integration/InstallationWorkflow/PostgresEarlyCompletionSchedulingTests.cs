using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// MIGRATION 20261125094516_SchedulingEffectiveBusyAfterCompletion and the early-completion capacity rules on a disposable PostgreSQL database (never
/// Supabase / production): Up–Down–Up, the effective-interval exclusion constraints on real rows, and races between completion and booking (TC16/TC17).
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresEarlyCompletionSchedulingTests
{
    private const string Previous = "20261124173442_PerPanelServicePackages";
    private const string Migration = "20261125094516_SchedulingEffectiveBusyAfterCompletion";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task<string> ConstraintAsync(PostgresTestDatabase database, string name)
    {
        await using var db = database.NewContext();
        return await db.Database.SqlQueryRaw<string>($"SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conname = '{name}'").SingleAsync();
    }

    [PostgresFact]
    public async Task MIGRATION_up_down_up_narrows_both_exclusions_to_the_effective_interval_and_keeps_the_schedule_columns()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(upToMigration: Previous);
        await using (var before = database.NewContext())
            Assert.Equal([Migration, "20261125104043_SchedulingOvertimeConflicts"], await before.Database.GetPendingMigrationsAsync());
        Assert.DoesNotContain("completed_at", await ConstraintAsync(database, "ex_technician_appointment_no_overlap"));

        await using (var up = database.NewContext()) await up.Database.MigrateAsync();
        Assert.Contains("completed_at", await ConstraintAsync(database, "ex_technician_appointment_no_overlap"));
        Assert.Contains("work_reported_at", await ConstraintAsync(database, "ex_installation_assignment_no_overlap"));
        await using (var db = database.NewContext())
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'installation_assignment' AND column_name = 'work_reported_at' AND is_nullable = 'YES') AS \"Value\"").SingleAsync());

        await using (var down = database.NewContext()) await down.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.DoesNotContain("completed_at", await ConstraintAsync(database, "ex_technician_appointment_no_overlap"));
        Assert.DoesNotContain("work_reported_at", await ConstraintAsync(database, "ex_installation_assignment_no_overlap"));
        await using (var again = database.NewContext()) await again.Database.MigrateAsync();
        await using (var check = database.NewContext())
        {
            Assert.Contains(Migration, await check.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        }
    }

    [PostgresFact]
    public async Task TC01_TC05_on_postgres_the_released_time_is_bookable_the_exclusion_still_guards_real_overlaps_and_down_is_refused()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (_, a) = await EarlyCompletionSchedulingTests.SurveyAsync(ctx);
        await EarlyCompletionSchedulingTests.CompleteAsync(ctx, a, At(8), At(9));
        var booked = await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(10));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");
        var b = booked.Value!.AppointmentId;

        // Reverting A to a non-completed state would make two live reservations overlap: the database refuses it (backstop for every write path).
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, await SqlStateAsync(database,
            $"UPDATE technician_appointment SET status = 'ACCEPTED', completed_at = NULL WHERE id = '{a.AppointmentId}'"));
        // Its original schedule is untouched.
        await using (var check = database.NewContext())
        {
            var row = await check.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
            Assert.Equal((At(10).ToUniversalTime(), At(11, 30).ToUniversalTime(), AppointmentStatuses.Completed), (row.StartsAt.ToUniversalTime(), row.EndsAt.ToUniversalTime(), row.Status));
            Assert.Equal(a.Technician.Id, (await check.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b)).ReservedTechnicianUserId);
        }

        // Down cannot restore the full-interval exclusion over these rows: refused, schema unchanged.
        await using (var down = database.NewContext())
            Assert.Contains("Down refused", (await Record.ExceptionAsync(() => down.GetService<IMigrator>().MigrateAsync(Previous)))?.ToString());
        await using (var after = database.NewContext())
            Assert.Equal(Migration, (await after.Database.GetAppliedMigrationsAsync()).Last());
    }

    [PostgresFact]
    public async Task TC16_survey_completion_racing_a_booking_of_the_released_time_is_consistent()
    {
        for (var round = 0; round < 3; round++)
        {
            await using var database = await PostgresTestDatabase.CreateAsync();
            await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
            var (_, a) = await EarlyCompletionSchedulingTests.SurveyAsync(ctx);
            var view = await EarlyCompletionSchedulingTests.StartAsync(ctx, a, At(8));
            var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Customer B");

            await using var completer = InstallationWorkflowTestContext.CreateOn(database.NewContext());
            await using var booker = InstallationWorkflowTestContext.CreateOn(database.NewContext());
            completer.Clock.Now = booker.Clock.Now = At(9).ToUniversalTime();
            var results = await Task.WhenAll(
                Task.Run(async () => (object)await completer.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(a.Technician.Id, a.AppointmentId, view.Revision), None)),
                Task.Run(async () => (object)await booker.Scheduling.Book().HandleAsync(
                    new Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10), null), None)));

            Assert.True(((Modules.Common.Results.OperationResult<OnSiteSurveyView>)results[0]).Succeeded);
            var booking = (Modules.Scheduling.PreSurveyAppointments.SchedulingResult<Modules.Scheduling.PreSurveyAppointments.BookingConfirmation>)results[1];
            await using var check = database.NewContext();
            var live = await check.TechnicianAppointments.AsNoTracking()
                .Where(x => x.ReservedTechnicianUserId == a.Technician.Id && x.Status != AppointmentStatuses.Cancelled).ToListAsync();
            // Either the booking saw the completion (and holds 10:00 next to the COMPLETED survey) or it was refused; never an overlap of live work.
            Assert.Equal(AppointmentStatuses.Completed, live.Single(x => x.Id == a.AppointmentId).Status);
            Assert.Equal(booking.Succeeded ? 2 : 1, live.Count);
        }
    }

    [PostgresFact]
    public async Task TC17_concurrent_bookings_of_the_released_slot_never_double_book_the_technician()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (_, a) = await EarlyCompletionSchedulingTests.SurveyAsync(ctx);
        await EarlyCompletionSchedulingTests.CompleteAsync(ctx, a, At(8), At(9));
        var requests = new List<SeededSurveyRequest>();
        for (var i = 0; i < 4; i++) requests.Add(await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, $"Racer {i}"));

        var bookers = requests.Select(_ => InstallationWorkflowTestContext.CreateOn(database.NewContext())).ToList();
        foreach (var b in bookers) b.Clock.Now = At(9).ToUniversalTime();
        var results = await Task.WhenAll(requests.Select((r, i) => Task.Run(() => bookers[i].Scheduling.Book().HandleAsync(
            new Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, At(10), null), None))));
        foreach (var b in bookers) await b.DisposeAsync();

        Assert.Single(results, r => r.Succeeded);   // one technician, one 10:00 slot
        await using var check = database.NewContext();
        Assert.Equal(1, await check.TechnicianAppointments.CountAsync(x => x.ReservedTechnicianUserId == a.Technician.Id && x.Status != AppointmentStatuses.Cancelled
                                                                           && x.Status != AppointmentStatuses.Completed));
    }

    [PostgresFact]
    public async Task TC16_installation_report_racing_a_booking_of_the_released_time_is_consistent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var i = await EarlyCompletionSchedulingTests.InProgressAsync(ctx);
        // Only the installation technician serves the zone for this booking.
        foreach (var other in await ctx.Db.TechnicianProfiles.Where(p => p.UserId != i.TechnicianId).ToListAsync()) other.IsSchedulingActive = false;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Customer B");

        await using var reporter = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        await using var booker = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        reporter.Clock.Now = booker.Clock.Now = At(11, 0, Wed).ToUniversalTime();
        var outcomes = await Task.WhenAll(
            Task.Run(async () => (await reporter.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Commissioned.", null, None)).Succeeded),
            Task.Run(async () => (await booker.Scheduling.Book().HandleAsync(
                new Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13, 0, Wed), null), None)).Succeeded));

        Assert.True(outcomes[0]);
        await using var check = database.NewContext();
        var assignment = await check.InstallationAssignments.AsNoTracking().SingleAsync(x => x.ProjectId == i.ProjectId);
        Assert.Equal(AssignmentStatuses.Accepted, assignment.Status);
        Assert.NotNull(assignment.WorkReportedAt);
        var booked = await check.TechnicianAppointments.AsNoTracking().Where(x => x.SurveyRequestId == request.Request.Id && x.Status != AppointmentStatuses.Cancelled).ToListAsync();
        // A booking only succeeds when it saw the released time (it went to the installation technician); otherwise it was refused.
        Assert.Equal(outcomes[1] ? 1 : 0, booked.Count);
        if (outcomes[1]) Assert.Equal(i.TechnicianId, booked[0].ReservedTechnicianUserId);
    }
}
