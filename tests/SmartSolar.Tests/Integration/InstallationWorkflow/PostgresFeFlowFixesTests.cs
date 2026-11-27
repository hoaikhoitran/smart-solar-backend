using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>FE Flow 1–4 fixes on a disposable PostgreSQL database (query translation, exclusion constraints and rows stored before the fixes).</summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresFeFlowFixesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task BUG01_a_survey_completed_a_day_early_frees_its_slot_and_the_database_agrees()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        ctx.Scheduling.OnSiteOptions.EarlyStartMinutes = 1440;
        var (_, a) = await EarlyCompletionSchedulingTests.SurveyAsync(ctx);
        await EarlyCompletionSchedulingTests.CompleteAsync(ctx, a, At(15, 0, Day.AddDays(-1)), At(15, 56, Day.AddDays(-1)));
        var booked = await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(10));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");
        // A still-active appointment in the same slot would be refused by the database itself.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var revert = new NpgsqlCommand($"UPDATE technician_appointment SET status = 'ACCEPTED', completed_at = NULL WHERE id = '{a.AppointmentId}'", connection);
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, (await Assert.ThrowsAsync<PostgresException>(() => revert.ExecuteNonQueryAsync())).SqlState);
    }

    [PostgresFact]
    public async Task BUG02_stored_plans_of_completed_projects_never_block_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var i = await EarlyCompletionSchedulingTests.InProgressAsync(ctx);
        ctx.Clock.Now = At(11, 0, Day.AddDays(1)).ToUniversalTime();
        Assert.True((await ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Done", null, None)).Succeeded);
        await PaymentPlanFlow.AcceptAsync(ctx, i.Flow, i.ProjectId);
        var plan = await ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.ContractId == i.Flow.ContractId);
        Assert.Equal(InstallationScheduleStatuses.Confirmed, plan.Status);
        // Rows as an older build may have left them: the completed project's plan stored PLANNED / RESCHEDULE_REQUIRED.
        foreach (var status in new[] { InstallationScheduleStatuses.Confirmed, InstallationScheduleStatuses.Planned, InstallationScheduleStatuses.RescheduleRequired })
        {
            await using (var db = database.NewContext())
                await db.Database.ExecuteSqlRawAsync(status == InstallationScheduleStatuses.RescheduleRequired
                    ? $"UPDATE installation_schedule_proposal SET status = '{status}', reschedule_reason_code = '{InstallationScheduleRescheduleReasons.TechnicianUnavailable}', reschedule_required_at = now() WHERE id = '{plan.Id}'"
                    : $"UPDATE installation_schedule_proposal SET status = '{status}' WHERE id = '{plan.Id}'");
            await using var read = database.NewContext();
            Assert.Empty(await new InstallationUnitOfWork(read).ListOverlappingLiveProposalsAsync(i.TechnicianId, Guid.NewGuid(), plan.StartsAt, plan.EndsAt, None));
        }
        // The project is still COMPLETED (no silent repair) and its assignment is not busy.
        await using var check = database.NewContext();
        Assert.Equal(InstallationStatuses.Completed, (await check.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId)).Status);
        Assert.Null((await ctx.Scheduling.Eligibility().CheckAsync(i.TechnicianId,
            new Modules.Scheduling.Domain.EligibilityRequest(plan.StartsAt, plan.StartsAt.AddHours(2), null), None)).Reason);
    }

    [PostgresFact]
    public async Task BUG04_quotation_lists_resolve_contract_ids_in_one_query_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        await using var read = database.NewContext();
        var ids = await new QuotationUnitOfWork(read).GetLiveContractIdsAsync([flow.Cart.Quotation.QuotationId, Guid.NewGuid()], None);
        Assert.Equal(flow.ContractId, Assert.Single(ids).Value);
        var list = await ctx.Quotations(read).ListForTechnicianAsync(flow.TechnicianId, flow.Cart.RequestId, None);
        Assert.Equal(flow.ContractId, Assert.Single(list.Value!).ContractId);
    }
}
