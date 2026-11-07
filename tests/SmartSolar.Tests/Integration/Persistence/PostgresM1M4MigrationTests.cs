using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSolar.Api.Middlewares;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Persistence;

/// <summary>
/// The M-1..M-4 migrations on a disposable PostgreSQL database with representative data: Down to the schema before them, legacy rows
/// written with that schema, Up again; then the backfill, defaults, legacy validity, every new constraint and index, the readiness probe and
/// the untouched existing data are checked.
/// </summary>
public sealed class PostgresM1M4MigrationTests
{
    private const string BeforeM1 = "20261030122441_AutomatedInstallationQuotation";
    private static readonly string[] M1ToM4 =
        ["20261030201506_SalesTerminalStates", "20261030201618_KnowledgeDocumentAudience", "20261030201748_InstallationEquipmentSubstitution",
         "20261030201824_ManufacturerWarrantyCoverage"];

    private static readonly Dictionary<string, string> CountQueries = new[] { "quotation", "quotation_line", "customer_contract", "installation_project",
        "installed_asset", "survey_request", "payment_attempt" }.ToDictionary(t => t, t => "SELECT count(*) AS \"Value\" FROM " + t);

    /// <summary>Row count of one of the fixed scenario tables (constant SQL per table, no interpolation).</summary>
    private static async Task<long> CountAsync(AppDbContext db, string table) => await db.Database.SqlQueryRaw<long>(CountQueries[table]).SingleAsync();

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, FormattableString sql)
    {
        await using var db = database.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        try { await db.Database.ExecuteSqlInterpolatedAsync(sql); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
        finally { await tx.RollbackAsync(); }
    }

    private static async Task<IReadOnlyList<string>?> ProbeAsync(PostgresTestDatabase database)
    {
        var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString)).BuildServiceProvider();
        var readiness = new SchemaReadiness(new EfSchemaMigrationProbe(services.GetRequiredService<IServiceScopeFactory>()), NullLogger<SchemaReadiness>.Instance,
            TimeProvider.System);
        await readiness.IsReadyAsync(CancellationToken.None);
        return readiness.Pending;
    }

    [PostgresFact]
    public async Task M1_to_M4_go_down_and_up_over_real_data_and_backfill_legacy_rows_safely()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var h = await ctx.HandedOverAsync();   // contract, paid, project, 13 assets (inverter with a 120-month catalog warranty)
        await using var db = database.NewContext();
        var tables = new[] { "quotation", "quotation_line", "customer_contract", "installation_project", "installed_asset", "survey_request", "payment_attempt" };
        var countsBefore = new Dictionary<string, long>();
        foreach (var t in tables) countsBefore[t] = await CountAsync(db, t);
        var inverterId = h.InverterId;
        var panelId = (await db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == h.ProjectId && a.AssetType == "PANEL")).Id;
        var requestId = (await db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId)).SurveyRequestId;

        // Down to the schema before M-1 (disposable database only).
        await db.GetService<IMigrator>().MigrateAsync(BeforeM1);
        // Readiness reports exactly the missing migrations: M-1..M-4 and the later M-7.
        Assert.Equal(M1ToM4.Append(M7), await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(M1ToM4.Append(M7), await ProbeAsync(database));
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'knowledge_document' AND column_name = 'audience') AS \"Value\"").SingleAsync());

        // Legacy rows written with the old schema: a cancelled request without closure data, a typed panel warranty, an old document and chat answer.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE installed_asset SET warranty_months = 60 WHERE id = {panelId}");
        var documentId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO knowledge_document (id, title, category, status, created_by_user_id, created_at, updated_at, revision) VALUES ({documentId}, 'Old FAQ', 'FAQ', 'REGISTERED', {h.AdminId}, now(), now(), 0)");
        var sessionId = Guid.NewGuid();
        var answerId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO chat_session (id, user_id, title, created_at, updated_at) VALUES ({sessionId}, {h.CustomerId}, 'Old', now(), now())");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO chat_message (id, session_id, role, content, status, created_at) VALUES ({answerId}, {sessionId}, 'ASSISTANT', 'old answer', 'COMPLETED', now())");

        // Up again.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty((await ProbeAsync(database))!);

        // Backfill and defaults: every duration is LEGACY_UNVERIFIED (including the inverter's, whose source column was dropped by Down: data
        // loss of a rollback, never upgraded to catalog data); assets without a duration keep no source; the old document is INTERNAL; the
        // old answer has no retrieval audience (so it is withheld from customers).
        var assets = await db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == h.ProjectId).ToListAsync();
        Assert.Equal("LEGACY_UNVERIFIED", assets.Single(a => a.Id == inverterId).WarrantySource);
        Assert.Equal(("LEGACY_UNVERIFIED", (int?)60), (assets.Single(a => a.Id == panelId).WarrantySource, assets.Single(a => a.Id == panelId).WarrantyMonths));
        Assert.All(assets.Where(a => a.WarrantyMonths is null), a => Assert.Null(a.WarrantySource));
        Assert.All(assets, a => Assert.Null(a.WarrantyStartsAt));
        Assert.Equal("INTERNAL", (await db.KnowledgeDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId)).Audience);
        Assert.Null((await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == answerId)).RetrievalAudience);
        foreach (var t in tables) Assert.Equal(countsBefore[t], await CountAsync(db, t));

        // A legacy Cancelled request without closure metadata is still valid; a new Closed one without it is not.
        Assert.Null(await SqlStateAsync(database, $"UPDATE survey_request SET status = 'Cancelled' WHERE id = {requestId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE survey_request SET status = 'Closed' WHERE id = {requestId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE survey_request SET status = 'Closed', closed_at = now(), closed_by_user_id = {h.SalesId}, closure_reason_code = 'BORED' WHERE id = {requestId}"));
        Assert.Null(await SqlStateAsync(database,
            $"UPDATE survey_request SET status = 'Closed', closed_at = now(), closed_by_user_id = {h.SalesId}, closure_reason_code = 'NO_RESPONSE' WHERE id = {requestId}"));
        var quotationId = (await db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId)).QuotationId;
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE quotation SET status = 'REJECTED' WHERE id = {quotationId}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database,
            $"UPDATE quotation SET status = 'CANCELLED', closed_at = now(), closed_by_user_id = {Guid.NewGuid()} WHERE id = {quotationId}"));

        // M-2: audience values; M-3: one active substitution per project (partial unique index); M-4: coverage and warranty rules.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE knowledge_document SET audience = 'PUBLIC' WHERE id = {documentId}"));
        var insertSubstitution = (Guid id) => (FormattableString)$"""
            INSERT INTO installation_equipment_substitution (id, project_id, contract_id, quotation_id, fingerprint, status, manager_user_id, manager_reason,
                manager_approved_at, created_at, updated_at, revision)
            VALUES ({id}, {h.ProjectId}, {h.ContractId}, {quotationId}, {new string('a', 32)}, 'CUSTOMER_APPROVAL_PENDING', {h.ManagerId}, 'x', now(), now(), now(), 0)
            """;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(insertSubstitution(Guid.NewGuid()));
            var duplicate = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync(insertSubstitution(Guid.NewGuid())));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException ?? duplicate).SqlState);
            await tx.RollbackAsync();
        }
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installed_asset SET warranty_source = NULL WHERE id = {panelId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installed_asset SET warranty_ends_at = now() WHERE id = {panelId}"));
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync();
        Assert.Contains("ux_installation_equipment_substitution_active", indexes);
        Assert.Contains("ix_survey_request_assigned_sale_status", indexes);
        Assert.Contains("ix_knowledge_document_status_audience", indexes);
        Assert.DoesNotContain("IX_survey_request_assigned_sale_id", indexes);   // replaced by the composite index
    }

    [PostgresFact]
    public async Task M1_down_refuses_while_terminal_rows_exist_and_leaves_the_schema_at_M1()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (survey, issued) = await SmartSolar.Tests.Unit.InstallationWorkflow.QuotationTests.IssuedAsync(ctx);
        Assert.True((await ctx.Quotations().RejectAsync(survey.Request.Customer.Id, issued.QuotationId, "no", CancellationToken.None)).Succeeded);
        await using var db = database.NewContext();
        await db.GetService<IMigrator>().MigrateAsync(M1ToM4[0]);   // M-2..M-4 rolled back (no data loss in this scenario)
        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(BeforeM1));
        Assert.NotNull(refused);   // the restored status check rejects the REJECTED row: remap terminal rows before a rollback
        await using var check = database.NewContext();
        Assert.Contains(M1ToM4[0], await check.Database.GetAppliedMigrationsAsync());
        Assert.Equal("REJECTED", await check.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM quotation WHERE id = {issued.QuotationId}").SingleAsync());
    }

    private const string M7 = "20261031043834_ContractDraftDiscard";

    [PostgresFact]
    public async Task M7_goes_down_and_up_over_draft_issued_signed_and_paid_contracts_without_changing_them()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var w = ctx.Installation;
        // Each journey books a survey on the fixture day: reset the fixed clock before each one.
        void Reset() => w.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        Reset();
        var draftReady = await SmartSolar.Tests.Unit.InstallationWorkflow.ContractTests.AcceptedQuotationAsync(w, "Draft customer");
        var draft = (await w.Contracts().CreateAsync(draftReady.Survey.Sales.Id, draftReady.QuotationId, "Terms", CancellationToken.None)).Value!;
        Reset();
        var issuedReady = await SmartSolar.Tests.Unit.InstallationWorkflow.ContractTests.AcceptedQuotationAsync(w, "Issued customer");
        var issuedDraft = (await w.Contracts().CreateAsync(issuedReady.Survey.Sales.Id, issuedReady.QuotationId, "Terms", CancellationToken.None)).Value!;
        Assert.True((await w.Contracts().IssueAsync(issuedReady.Survey.Sales.Id, issuedDraft.ContractId, issuedDraft.Revision, CancellationToken.None)).Succeeded);
        Reset();
        var (_, signed) = await SmartSolar.Tests.Unit.InstallationWorkflow.ContractTests.SignedAsync(w, "Signed customer");
        Reset();
        var h = await ctx.HandedOverAsync();   // a PAID contract with its project
        await using var db = database.NewContext();
        const string snapshotSql = "SELECT id::text || '|' || status || '|' || coalesce(canonical_hash, '') || '|' || coalesce(issued_at::text, '') || '|' || coalesce(signed_at::text, '') || '|' || coalesce(paid_at::text, '') || '|' || revision::text AS \"Value\" FROM customer_contract ORDER BY id";
        var before = await db.Database.SqlQueryRaw<string>(snapshotSql).ToListAsync();
        Assert.Equal(new[] { "DRAFT", "ISSUED", "PAID", "SIGNED" }, before.Select(r => r.Split('|')[1]).Order());

        // Down to M-4: the readiness probe reports exactly M-7; Up restores it; history lists all seven pending-era migrations in order.
        await db.GetService<IMigrator>().MigrateAsync(M1ToM4[^1]);
        Assert.Equal([M7], await ProbeAsync(database));
        await db.Database.MigrateAsync();
        Assert.Empty((await ProbeAsync(database))!);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(M7, applied[^1]);
        Assert.Equal(new[] { "20261021164225_AddCommuneReferencePoints", "20261030122441_AutomatedInstallationQuotation" }.Concat(M1ToM4).Append(M7),
            applied.SkipWhile(m => m != "20261021164225_AddCommuneReferencePoints"));

        // Every existing contract is unchanged; none became DISCARDED or got discard data.
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshotSql).ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM customer_contract WHERE discarded_at IS NOT NULL OR discard_reason IS NOT NULL OR discarded_by_user_id IS NOT NULL").SingleAsync());

        // Constraints on real data.
        var draftId = draft.ContractId;
        var signedId = signed.ContractId;
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE customer_contract SET status = 'DISCARDED' WHERE id = {draftId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE customer_contract SET discarded_at = now(), discarded_by_user_id = {h.SalesId}, discard_reason = 'x' WHERE id = {signedId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE customer_contract SET status = 'DISCARDED', discarded_at = now(), discarded_by_user_id = {h.SalesId}, discard_reason = 'x' WHERE id = {signedId}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database,
            $"UPDATE customer_contract SET status = 'DISCARDED', discarded_at = now(), discarded_by_user_id = {Guid.NewGuid()}, discard_reason = 'x' WHERE id = {draftId}"));

        // The real workflow: discard the DRAFT, close its request; M-7 Down is then refused (a DISCARDED row) and the schema stays at M-7.
        Assert.True((await w.Contracts().DiscardAsync(draftReady.Survey.Sales.Id, draftId, draft.Revision, "Customer withdrew", CancellationToken.None)).Succeeded);
        Assert.True((await w.Quotations().CloseRequestAsync(draftReady.Survey.Sales.Id, draftReady.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null,
            CancellationToken.None)).Succeeded);
        Assert.NotNull(await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(M1ToM4[^1])));
        await using var check = database.NewContext();
        Assert.Equal(M7, (await check.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal("DISCARDED", await check.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM customer_contract WHERE id = {draftId}").SingleAsync());
    }
}

