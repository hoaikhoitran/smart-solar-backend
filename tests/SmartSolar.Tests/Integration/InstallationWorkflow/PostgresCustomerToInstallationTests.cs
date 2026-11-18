using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Direct survey booking and technician-agreed installation schedules on a disposable PostgreSQL database: concurrent callers never create
/// two survey requests, two live schedules, two projects or two assignments; the constraints hold; the migration backfills every existing
/// request and its Down refuses (changing nothing) while new-flow data exists.
/// </summary>
public sealed class PostgresCustomerToInstallationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261117074228_SimplifiedCommercialFlow";
    private const string Migration = "20261117092018_DirectBookingAndInstallationSchedule";
    private const string Compatibility = "20261117101818_SurveyRequestSiteCompatibility";
    private const string CostControl = "20261117131913_CloudRagCostControl";
    private const string SlotOffers = "20261117171006_InstallationSlotOffers";
    private const string TechnicianQuotation = "20261117185657_AfterSalesTechnicianQuotation";
    private const string OnSiteSales = "20261118102011_TechnicianLedOnSiteSales";

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var db = database.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_direct_bookings_of_one_site_create_one_request_and_one_appointment()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        await ctx.Scheduling.SeedTechnicianAsync("Tech A", BinhTan);
        await ctx.Scheduling.SeedTechnicianAsync("Tech B", BinhTan);
        var (customer, site) = await DirectSurveyBookingTests.CustomerWithSiteAsync(ctx);

        var results = await RaceAsync(database, 6, (db, _) =>
        {
            var w = InstallationWorkflowTestContext.CreateOn(db);
            w.Clock.Now = ctx.Clock.Now;
            return w.DirectBooking().HandleAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, null, null), None);
        });

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorCode + " " + r.Message));
        Assert.Single(results.Select(r => r.Value!.SurveyRequestId).Distinct());
        Assert.Single(results, r => r.Value!.Booking.Created);
        await using var check = database.NewContext();
        Assert.Equal(1, await check.SurveyRequests.CountAsync(r => r.PropertySiteId == site.Id));
        Assert.Equal(1, await check.TechnicianAppointments.CountAsync());
        Assert.Equal(0, await check.PreSurveys.CountAsync(p => p.PropertyId == site.Id));
    }

    [PostgresFact]
    public async Task Concurrent_plans_leave_one_live_schedule_and_concurrent_confirmations_one_project_and_assignment()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;

        var plans = await RaceAsync(database, 4, (db, i) =>
        {
            var w = InstallationWorkflowTestContext.CreateOn(db);
            w.Clock.Now = ctx.Clock.Now;
            return w.Schedules(db).ProposeAsync(signed.Cart.TechnicianId, false, contractId,
                InstallationScheduleTests.Plan(At(8, 0, Day.AddDays(1 + i)), At(12, 0, Day.AddDays(1 + i)), reason: "x"), None);
        });
        Assert.Contains(plans, p => p.Succeeded);
        await using (var check = database.NewContext())
            Assert.Equal(1, await check.InstallationScheduleProposals.CountAsync(p => p.ContractId == contractId && p.Status != InstallationScheduleStatuses.Cancelled));

        // Paid (cash, follow-up disabled here), then every trigger races: payment follow-up, sweep, manual retries.
        var payments = ctx.PaymentsWithCash();
        Assert.True((await payments.SelectCashAsync(signed.Cart.CustomerId, contractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, Modules.Files.FilePurposes.PaymentEvidence);
        Assert.True((await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(signed.Contract.TotalAmount, "PT-RACE", evidence, null), None)).Succeeded);

        var confirmations = await RaceAsync(database, 6, async (db, _) =>
        {
            var w = InstallationWorkflowTestContext.CreateOn(db);
            w.Clock.Now = ctx.Clock.Now;
            return await w.Schedules(db).ConfirmForContractAsync(contractId, None);
        });
        // Late callers may see a transient "kept changing" answer; one more pass (the sweep) settles it.
        await ctx.Schedules().SweepAsync(25, None);

        await using var after = database.NewContext();
        var project = await after.InstallationProjects.SingleAsync(p => p.ContractId == contractId);
        Assert.Equal(InstallationStatuses.Scheduled, project.Status);
        Assert.Equal(1, await after.InstallationAssignments.CountAsync(a => a.ProjectId == project.Id));
        Assert.Equal(AssignmentStatuses.Accepted, (await after.InstallationAssignments.SingleAsync(a => a.ProjectId == project.Id)).Status);
        var live = await after.InstallationScheduleProposals.SingleAsync(p => p.ContractId == contractId && p.Status != InstallationScheduleStatuses.Cancelled);
        Assert.Equal((InstallationScheduleStatuses.Confirmed, project.Id), (live.Status, live.ProjectId!.Value));
        Assert.All(confirmations, c => Assert.True(c.Succeeded || c.ErrorCode == InstallationScheduleErrorCodes.StaleRevision, c.ErrorCode));
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_the_source_rules_and_the_one_open_direct_booking_and_one_live_schedule_indexes()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var direct = await DirectSurveyBookingTests.ConfirmedDirectSurveyAsync(ctx);
        var requestId = direct.Request.Request.Id;

        // A direct booking never carries a pre-survey, a pre-survey request always does; unknown sources are refused.
        var anyPreSurvey = (await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Option A")).PreSurvey.Id;
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE survey_request SET pre_survey_id = '{anyPreSurvey}' WHERE id = '{requestId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE survey_request SET source = 'PRE_SURVEY' WHERE id = '{requestId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE survey_request SET source = 'WALK_IN' WHERE id = '{requestId}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database, $"UPDATE survey_request SET property_site_id = gen_random_uuid() WHERE id = '{requestId}'"));
        // A second open direct booking for the same site.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database, $"""
            INSERT INTO survey_request (id, pre_survey_id, property_site_id, source, status, submitted_at)
            VALUES (gen_random_uuid(), NULL, '{direct.Request.Site.Id}', 'DIRECT_BOOKING', 'Pending', now())
            """));

        // One live schedule per contract; statuses and confirmation fields are checked.
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx, "Schedule customer");
        var plan = (await ctx.Schedules().ProposeAsync(signed.Cart.TechnicianId, false, signed.Contract.ContractId, InstallationScheduleTests.Plan(), None)).Value!;
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database, $"""
            INSERT INTO installation_schedule_proposal (id, contract_id, survey_request_id, customer_user_id, property_site_id, technician_user_id, starts_at, ends_at,
                status, customer_agreement_note, created_by_user_id, created_at, updated_at, revision)
            SELECT gen_random_uuid(), contract_id, survey_request_id, customer_user_id, property_site_id, technician_user_id, starts_at + interval '1 day', ends_at + interval '1 day',
                'PLANNED', 'again', created_by_user_id, now(), now(), 0 FROM installation_schedule_proposal WHERE id = '{plan.ScheduleId}'
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_schedule_proposal SET status = 'CONFIRMED' WHERE id = '{plan.ScheduleId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_schedule_proposal SET status = 'RESCHEDULE_REQUIRED' WHERE id = '{plan.ScheduleId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_schedule_proposal SET ends_at = starts_at WHERE id = '{plan.ScheduleId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_schedule_proposal SET status = 'DONE' WHERE id = '{plan.ScheduleId}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database, $"UPDATE installation_schedule_proposal SET technician_user_id = gen_random_uuid() WHERE id = '{plan.ScheduleId}'"));
    }

    [PostgresFact]
    public async Task The_migration_backfills_existing_requests_and_Down_is_refused_while_new_flow_data_exists()
    {
        // Existing data: an Option A request on the schema BEFORE this migration (written by the current model, then this migration goes down).
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var seeded = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Legacy customer");
        await using var db = database.NewContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'survey_request' AND column_name = 'property_site_id') AS \"Value\"").SingleAsync());
        Assert.Equal([Migration, Compatibility, CostControl, SlotOffers, TechnicianQuotation, OnSiteSales], await db.Database.GetPendingMigrationsAsync());

        await db.Database.MigrateAsync();
        await using (var check = database.NewContext())
        {
            var row = await check.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == seeded.Request.Id);
            Assert.Equal((seeded.Site.Id, SurveyRequestSources.PreSurvey, (Guid?)seeded.PreSurvey.Id), (row.PropertySiteId, row.Source, row.PreSurveyId));
            Assert.Equal("NO", await check.Database.SqlQueryRaw<string>(
                "SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'survey_request' AND column_name = 'property_site_id'").SingleAsync());
        }

        // New-flow data: a direct booking. Down refuses and changes nothing.
        await DirectSurveyBookingTests.ConfirmedDirectSurveyAsync(ctx);
        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.NotNull(refused);
        Assert.Contains("DirectBookingAndInstallationSchedule rollback refused", refused.ToString());
        await using var after = database.NewContext();
        Assert.Equal(Migration, (await after.Database.GetAppliedMigrationsAsync()).Last());
        await after.Database.MigrateAsync();
        Assert.Equal(2, await after.SurveyRequests.CountAsync());
    }

    [PostgresFact]
    public async Task An_older_api_build_that_omits_the_site_still_submits_pre_surveys_and_direct_bookings_still_need_a_site()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var user = await ctx.Scheduling.Inner.SeedUserAsync("Old build customer");
        var customer = await ctx.Scheduling.Inner.SeedCustomerAsync(user.Id);
        var site = await ctx.Scheduling.Inner.SeedPropertySiteAsync(customer.Id);
        var preSurvey = await ctx.Scheduling.Inner.SeedPreSurveyAsync(site.Id, Modules.PreSurvey.Enums.PreSurveyStatus.Submitted);

        // Exactly the columns the previous EF model writes (no property_site_id, no source).
        await using (var db = database.NewContext())
            await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO survey_request (id, pre_survey_id, assigned_sale_id, status, submitted_at, assigned_at, scheduled_at, sales_note)
                VALUES (gen_random_uuid(), '{preSurvey.Id}', NULL, 'Pending', now(), NULL, NULL, NULL)
                """);
        await using var check = database.NewContext();
        var row = await check.SurveyRequests.AsNoTracking().SingleAsync(r => r.PreSurveyId == preSurvey.Id);
        Assert.Equal((site.Id, SurveyRequestSources.PreSurvey), (row.PropertySiteId, row.Source));
        // The trigger never invents a site for a request without a pre-survey.
        Assert.Equal(PostgresErrorCodes.NotNullViolation, await SqlStateAsync(database,
            "INSERT INTO survey_request (id, source, status, submitted_at) VALUES (gen_random_uuid(), 'DIRECT_BOOKING', 'Pending', now())"));
    }
}
