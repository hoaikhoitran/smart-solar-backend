using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// The AI budget on a disposable PostgreSQL database with racing callers, each on its own connection (as separate API replicas): the shared
/// budget, the global concurrency ceiling and the in-flight duplicate rule hold exactly, a settlement applies once, and the migration's Down
/// refuses (changing nothing) while the cost ledger holds rows.
/// </summary>
public sealed class PostgresAiCostControlTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Migration = "20261117131913_CloudRagCostControl";
    private const string Previous = "20261117101818_SurveyRequestSiteCompatibility";
    private static readonly RagOptions Limits = new() { DailyTokensPerUser = 10_000_000, GlobalDailyTokens = 100_000_000 };

    private static AiAdmissionRequest Request(Guid user, decimal usd, DateTimeOffset now, string? client = null, int userConcurrency = 1, int global = 50,
        decimal daily = 1m, decimal monthly = 4m)
        => new(user, "Customer", DateOnly.FromDateTime(now.UtcDateTime), 100, usd, true, client, 0, 30, userConcurrency, global, daily, monthly, now, now.AddMinutes(2));

    private static async Task<List<Guid>> UsersAsync(InstallationWorkflowTestContext ctx, int count)
    {
        var users = new List<Guid>();
        for (var i = 0; i < count; i++) users.Add((await ctx.Scheduling.Inner.SeedUserAsync($"AI user {i}")).Id);
        return users;
    }

    [PostgresFact]
    public async Task Racing_replicas_never_reserve_more_than_the_shared_daily_budget()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var users = await UsersAsync(ctx, 20);
        var now = DateTimeOffset.UtcNow;

        // 20 questions of 0.10 USD each against a 1.00 USD day: exactly 10 may be admitted, whatever the interleaving.
        var results = await RaceAsync(database, 20, (db, i) => new KnowledgeUnitOfWork(db).TryAdmitAsync(Request(users[i], 0.10m, now), Limits, None));

        Assert.Equal(10, results.Count(r => r.Admitted));
        Assert.All(results.Where(r => !r.Admitted), r => Assert.Equal(AiCostErrorCodes.BudgetExceeded, r.Limit));
        await using var check = database.NewContext();
        var day = await check.AiBudgetPeriods.SingleAsync(p => p.PeriodKind == AiBudgetPeriodKinds.Day);
        Assert.Equal((1.00m, 10, 10), (day.ReservedUsd, day.Requests, day.Blocked));
        Assert.Equal(10, await check.AiUsageLedger.CountAsync(e => e.Status == AiUsageStatuses.Reserved));
        Assert.Equal(1.00m, (await check.AiBudgetPeriods.SingleAsync(p => p.PeriodKind == AiBudgetPeriodKinds.Month)).ReservedUsd);
    }

    [PostgresFact]
    public async Task The_global_concurrency_ceiling_and_the_in_flight_duplicate_rule_hold_under_races()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var users = await UsersAsync(ctx, 10);
        var now = DateTimeOffset.UtcNow;

        var concurrent = await RaceAsync(database, 10, (db, i) => new KnowledgeUnitOfWork(db).TryAdmitAsync(Request(users[i], 0.001m, now, global: 3), Limits, None));
        Assert.Equal(3, concurrent.Count(r => r.Admitted));
        Assert.All(concurrent.Where(r => !r.Admitted), r => Assert.Equal(AiCostErrorCodes.GlobalBusy, r.Limit));

        // The same client message id 6 times at once from one user (double click on two replicas): one admission, never two.
        var single = users[9];
        var duplicates = await RaceAsync(database, 6, async (db, _) =>
        {
            try
            {
                return (await new KnowledgeUnitOfWork(db).TryAdmitAsync(Request(single, 0m, now, client: "same-question", userConcurrency: 2, global: 50), Limits, None)).Limit
                       ?? "ADMITTED";
            }
            catch (PersistenceConflictException)
            {
                return AiCostErrorCodes.RequestInProgress;   // the unique index stopped it (mapped to the same error by ChatService)
            }
        });
        Assert.Single(duplicates, r => r == "ADMITTED");
        Assert.All(duplicates.Where(r => r != "ADMITTED"), r => Assert.Contains(r, new[] { AiCostErrorCodes.RequestInProgress, AiCostErrorCodes.Concurrency }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.RagUsageReservations.CountAsync(r => r.ClientMessageId == "same-question"));
    }

    [PostgresFact]
    public async Task A_settlement_applies_once_even_when_replicas_settle_the_same_reservation_together()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var user = (await UsersAsync(ctx, 1))[0];
        var now = DateTimeOffset.UtcNow;
        var admission = await new KnowledgeUnitOfWork(ctx.Db).TryAdmitAsync(Request(user, 0.001m, now), Limits, None);
        Assert.True(admission.Admitted);
        var settlement = new AiChatSettlement(AiUsageOutcomes.Answered, false, null, "openai", "text-embedding-3-small", 12, 0.00000024m, "openai", "gpt-6-luna",
            1200, 1024, 80, 0, 0.00006784m, true, 900, now.AddSeconds(2));

        await RaceAsync(database, 4, (db, _) => new KnowledgeUnitOfWork(db).SettleChatAsync(admission.ReservationId!.Value, 1200, 80, settlement, None));

        await using var check = database.NewContext();
        var day = await check.AiBudgetPeriods.SingleAsync(p => p.PeriodKind == AiBudgetPeriodKinds.Day);
        Assert.Equal((0m, 0.00006808m, 0m), (day.ReservedUsd, day.SettledUsd, day.UncertainUsd));
        var row = await check.AiUsageLedger.SingleAsync();
        Assert.Equal((AiUsageStatuses.Settled, 0.00006808m), (row.Status, row.CostUsd));
        var usage = await check.RagUsageDaily.SingleAsync(u => u.UserId == user);
        Assert.Equal((1200L, 80L), (usage.InputTokens, usage.OutputTokens));
    }

    [PostgresFact]
    public async Task The_migration_goes_down_and_up_on_an_empty_ledger_and_refuses_down_while_it_holds_rows()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        await using var db = database.NewContext();
        Assert.Equal(Migration, (await db.Database.GetAppliedMigrationsAsync()).Last());

        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('ai_usage_ledger') IS NOT NULL AS \"Value\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(Migration, (await db.Database.GetAppliedMigrationsAsync()).Last());

        var user = (await UsersAsync(ctx, 1))[0];
        Assert.True((await new KnowledgeUnitOfWork(ctx.Db).TryAdmitAsync(Request(user, 0.001m, DateTimeOffset.UtcNow), Limits, None)).Admitted);
        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Contains("CloudRagCostControl rollback refused", refused?.ToString());
        await using var check = database.NewContext();
        Assert.Equal(Migration, (await check.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(1, await check.AiUsageLedger.CountAsync());
    }
}
