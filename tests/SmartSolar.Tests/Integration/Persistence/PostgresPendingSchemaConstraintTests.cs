using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Persistence;

/// <summary>
/// The PENDING_MANUAL_MIGRATION review script applied to a disposable local PostgreSQL database:
/// the catalog must contain exactly the EF model's constraints, and PostgreSQL must enforce them.
/// </summary>
public sealed class PostgresPendingSchemaConstraintTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record ModelForeignKey(string Table, string Columns, string Principal, char DeleteRule);

    private static IReadOnlyList<ModelForeignKey> ModelForeignKeys()
        => PendingSchemaScript.PendingEntityTypes().SelectMany(e =>
        {
            var table = e.GetTableName()!;
            var store = StoreObjectIdentifier.Table(table, e.GetSchema());
            return e.GetForeignKeys().Select(fk => new ModelForeignKey(table, string.Join(",", fk.Properties.Select(p => p.GetColumnName(store))),
                fk.PrincipalEntityType.GetTableName()!, fk.DeleteBehavior switch { DeleteBehavior.Cascade => 'c', DeleteBehavior.Restrict => 'r', DeleteBehavior.SetNull => 'n', _ => 'a' }));
        }).ToList();

    private static async Task<List<T>> QueryAsync<T>(PostgresTestDatabase database, string sql, Func<NpgsqlDataReader, T> map, params object[] args)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Runs <paramref name="sql"/> in a transaction that is always rolled back; returns the SQLSTATE (null on success).</summary>
    private static async Task<string?> TryInRolledBackTransactionAsync(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [PostgresFact]
    public async Task Catalog_contains_every_foreign_key_check_unique_index_and_the_exclusion_constraint_of_the_model()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        var tables = PendingSchemaScript.PendingTables().ToArray();

        var actual = await QueryAsync(database, """
            SELECT c.conrelid::regclass::text, c.confrelid::regclass::text, c.confdeltype::text,
                   (SELECT string_agg(a.attname, ',' ORDER BY array_position(c.conkey, a.attnum)) FROM pg_attribute a
                     WHERE a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey))
            FROM pg_constraint c WHERE c.contype = 'f' AND c.conrelid::regclass::text = ANY ($1)
            """, r => new ModelForeignKey(r.GetString(0), r.GetString(3), r.GetString(1), r.GetString(2)[0]), (object)tables);
        var expected = ModelForeignKeys();
        Assert.Equal(83, expected.Count);
        Assert.Equal(expected.OrderBy(k => k.ToString()), actual.OrderBy(k => k.ToString()));

        var checks = await QueryAsync(database, "SELECT conname FROM pg_constraint WHERE contype = 'c' AND conrelid::regclass::text = ANY ($1)", r => r.GetString(0), (object)tables);
        var modelChecks = PendingSchemaScript.PendingEntityTypes().SelectMany(e => e.GetCheckConstraints()).Select(c => c.Name!).ToList();
        Assert.NotEmpty(modelChecks);
        Assert.Equal(modelChecks.Order(), checks.Order());

        var uniques = await QueryAsync(database, """
            SELECT i.relname FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid
            WHERE x.indisunique AND NOT x.indisprimary AND x.indrelid::regclass::text = ANY ($1)
            """, r => r.GetString(0), (object)tables);
        // Unique indexes plus alternate keys (unique constraints are backed by unique indexes in PostgreSQL).
        var modelUniques = PendingSchemaScript.PendingEntityTypes().SelectMany(e =>
        {
            var store = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
            return e.GetIndexes().Where(i => i.IsUnique).Select(i => i.GetDatabaseName(store)!)
                .Concat(e.GetKeys().Where(k => !k.IsPrimaryKey()).Select(k => k.GetName(store)!));
        }).ToList();
        Assert.Equal(modelUniques.Order(), uniques.Order());

        var exclusion = await QueryAsync(database, "SELECT conname FROM pg_constraint WHERE contype = 'x' AND conrelid = 'installation_shift_assignment'::regclass", r => r.GetString(0));
        Assert.Equal(["ex_installation_shift_assignment_no_overlap"], exclusion);
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_every_foreign_key_restricts_deletes_and_rejects_invalid_checks()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        // A realistic graph: Flow 1 → on-site survey → quotation → signed and paid contract → scheduled installation.
        var (paid, project, leader, _) = await InstallationTests.ScheduledAsync(ctx);
        await PopulateEveryTableAsync(ctx, paid, project.ProjectId, leader);

        var enforced = 0;
        var skippedEmpty = new List<string>();
        foreach (var fk in ModelForeignKeys().Where(k => !k.Columns.Contains(',')))
        {
            var hasRow = (await QueryAsync(database, $"SELECT EXISTS (SELECT 1 FROM {fk.Table} WHERE {fk.Columns} IS NOT NULL)", r => r.GetBoolean(0)))[0];
            if (!hasRow) { skippedEmpty.Add($"{fk.Table}.{fk.Columns}"); continue; }
            var state = await TryInRolledBackTransactionAsync(database,
                $"UPDATE {fk.Table} SET {fk.Columns} = gen_random_uuid() WHERE ctid = (SELECT ctid FROM {fk.Table} WHERE {fk.Columns} IS NOT NULL LIMIT 1)");
            Assert.True(state == PostgresErrorCodes.ForeignKeyViolation, $"{fk.Table}.{fk.Columns} → {fk.Principal}: expected 23503, got {state ?? "success"}");
            enforced++;
        }
        output.WriteLine($"Foreign keys violated and rejected: {enforced}; not exercised (no rows in the scenario): {string.Join(", ", skippedEmpty)}");
        // Most of the graph is populated by the scenario; the rest is covered by the catalog test.
        Assert.True(skippedEmpty.Count == 0, $"Only {enforced} foreign keys exercised; empty: {string.Join(", ", skippedEmpty)}");

        // Composite FKs: a document cannot point at a missing version or at another document's version.
        var otherVersion = (await QueryAsync(database, "SELECT v.id FROM knowledge_document_version v JOIN knowledge_document d ON d.id <> v.document_id LIMIT 1",
            r => r.GetGuid(0))).FirstOrDefault();
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await TryInRolledBackTransactionAsync(database,
            "UPDATE knowledge_document SET latest_version_id = gen_random_uuid()"));
        if (otherVersion != Guid.Empty)
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await TryInRolledBackTransactionAsync(database,
                $"UPDATE knowledge_document SET published_version_id = '{otherVersion}', status = 'PUBLISHED' WHERE id <> (SELECT document_id FROM knowledge_document_version WHERE id = '{otherVersion}')"));
        // A VERIFIED file must carry a lowercase SHA-256.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await TryInRolledBackTransactionAsync(database,
            "UPDATE file_reference SET verified_checksum_sha256 = NULL WHERE status = 'VERIFIED'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await TryInRolledBackTransactionAsync(database,
            "UPDATE file_reference SET verified_checksum_sha256 = upper(verified_checksum_sha256) WHERE status = 'VERIFIED'"));

        // RESTRICT: a project with milestones, team, shifts and events cannot be deleted.
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await TryInRolledBackTransactionAsync(database, $"DELETE FROM installation_project WHERE id = '{project.ProjectId}'"));
        // CHECK constraints.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await TryInRolledBackTransactionAsync(database,
            $"UPDATE installation_shift_assignment SET ends_at = starts_at WHERE technician_user_id = '{leader}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await TryInRolledBackTransactionAsync(database, "UPDATE payment_attempt SET status = 'SOMETHING'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await TryInRolledBackTransactionAsync(database, "UPDATE installation_milestone SET status = 'DONE'"));
        // UNIQUE: exactly one leader per team.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await TryInRolledBackTransactionAsync(database,
            $"UPDATE installation_team_member SET is_leader = true WHERE project_id = '{project.ProjectId}'"));
        // Exclusion: one technician cannot hold two overlapping ACTIVE installation assignments.
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, await TryInRolledBackTransactionAsync(database, $"""
            WITH source AS (SELECT * FROM installation_shift_assignment WHERE technician_user_id = '{leader}' AND status = 'ACTIVE' LIMIT 1),
                 shift AS (
                     INSERT INTO installation_shift (id, project_id, local_date, local_start, local_end, starts_at, ends_at, status, created_at)
                     SELECT gen_random_uuid(), s.project_id, s.local_date, s.local_start + interval '1 hour', s.local_end + interval '1 hour',
                            s.starts_at + interval '1 hour', s.ends_at + interval '1 hour', 'ACTIVE', now()
                     FROM installation_shift s JOIN source ON source.shift_id = s.id
                     RETURNING id, project_id, starts_at, ends_at)
            INSERT INTO installation_shift_assignment (id, shift_id, project_id, technician_user_id, starts_at, ends_at, status, created_at)
            SELECT gen_random_uuid(), shift.id, shift.project_id, '{leader}', shift.starts_at, shift.ends_at, 'ACTIVE', now() FROM shift
            """));
    }

    [PostgresFact]
    public async Task A_failure_inside_the_schedule_confirmation_rolls_back_every_row()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var leader = await ctx.Scheduling.SeedTechnicianAsync("Leader", District1);
        var member = await ctx.Scheduling.SeedTechnicianAsync("Member", District7);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        // Simulate a crash after shifts and assignments are written but before the team is saved.
        await using (var db = database.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION test_fail_team_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END $$;
                CREATE TRIGGER test_fail_team_insert BEFORE INSERT ON installation_team_member FOR EACH ROW EXECUTE FUNCTION test_fail_team_insert();
                """);
        }
        IReadOnlyList<ShiftInput> shifts = [new ShiftInput(Day.AddDays(1), new TimeOnly(8, 0), new TimeOnly(12, 0)), new ShiftInput(Day.AddDays(2), new TimeOnly(8, 0), new TimeOnly(12, 0))];

        await using (var db = database.NewContext())
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Installations(db).ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [leader.Id, member.Id], leader.Id, shifts, None));

        await using var check = database.NewContext();
        Assert.Equal(0, await check.InstallationShifts.CountAsync(s => s.ProjectId == project.ProjectId));
        Assert.Equal(0, await check.InstallationShiftAssignments.CountAsync(a => a.ProjectId == project.ProjectId));
        Assert.Equal(0, await check.InstallationTeamMembers.CountAsync(m => m.ProjectId == project.ProjectId));
        Assert.Equal(0, await check.InstallationEvents.CountAsync(e => e.ProjectId == project.ProjectId && e.EventType == "SCHEDULE_CONFIRMED"));
        var stored = await check.InstallationProjects.SingleAsync(p => p.Id == project.ProjectId);
        Assert.Equal(InstallationStatuses.Planning, stored.Status);
        Assert.Equal(project.Revision, stored.Revision);
        Assert.Equal(0, await check.TechnicianScheduleRecalculations.CountAsync(r => r.TechnicianUserId == leader.Id || r.TechnicianUserId == member.Id));
    }

    /// <summary>Drives the installation to handover (checklist, evidence, assets, acceptance) and asks the knowledge base one question.</summary>
    private static async Task PopulateEveryTableAsync(InstallationWorkflowTestContext ctx, InstallationTests.Paid paid, Guid projectId, Guid leader)
    {
        var installs = ctx.Installations();
        var view = (await installs.GetForManagerAsync(projectId, None)).Value!;
        var document = await ctx.VerifiedFileAsync(paid.ManagerId, SmartSolar.Modules.Files.FilePurposes.ProjectDocument, "pdf");
        var item = view.Checklist![0];
        var satisfied = await installs.UpdateChecklistItemAsync(paid.ManagerId, projectId, item.ItemId, view.Revision,
            new ChecklistUpdate(item.IsRequired, ChecklistStatuses.Satisfied, document, "Approved"), None);
        Assert.True(satisfied.Succeeded, satisfied.Message);

        ctx.Clock.Now = At(8, 30, Day.AddDays(1)).ToUniversalTime();
        Assert.True((await installs.StartProjectAsync(leader, projectId, None)).Succeeded);
        await InstallationTests.CompleteAllMilestonesAsync(ctx, projectId, leader);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, leader)).Succeeded);
        var catalogPanel = await ctx.ProductAsync("PG-ASSET-PNL", 2_000_000m);
        Assert.True((await installs.RegisterAssetsAsync(leader, projectId,
            [new AssetInput("PANEL", catalogPanel.Id, "Jinko", "Tiger Neo 550", "JK-CATALOG-001", "R2-C1", 144)], None)).Succeeded);
        Assert.True((await installs.RequestAcceptanceAsync(leader, projectId, "All panels installed.", None)).Succeeded);
        var accepted = await installs.DecideAcceptanceAsync(paid.CustomerId, projectId, true, null, None);
        Assert.True(accepted.Succeeded, accepted.Message);

        var rag = new FakeRagServiceClient();
        var options = new SmartSolar.Modules.KnowledgeBase.RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32) };
        var uow = new SmartSolar.Infrastructure.Persistence.KnowledgeBase.KnowledgeUnitOfWork(ctx.Db);
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("KB admin")).Id;
        var file = await ctx.VerifiedFileAsync(admin, SmartSolar.Modules.Files.FilePurposes.KnowledgeDocument, "pdf");
        var documents = new SmartSolar.Modules.KnowledgeBase.KnowledgeDocumentHandlers(uow, ctx.Files(), options, ctx.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmartSolar.Modules.KnowledgeBase.KnowledgeDocumentHandlers>.Instance);
        var doc = (await documents.RegisterAsync(admin, file, "Warranty", "WARRANTY", null, None)).Value!;
        await new SmartSolar.Modules.KnowledgeBase.KnowledgeIngestionService(uow, new SmartSolar.Infrastructure.Persistence.FileStore(ctx.Db), rag, options, ctx.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmartSolar.Modules.KnowledgeBase.KnowledgeIngestionService>.Instance).RunDueAsync(10, None);
        var ready = (await documents.GetAsync(doc.DocumentId, None)).Value!;
        Assert.True((await documents.PublishAsync(doc.DocumentId, ready.Versions![0].VersionId, ready.Revision, None)).Succeeded);
        var chat = new SmartSolar.Modules.KnowledgeBase.ChatService(uow, rag, options, ctx.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmartSolar.Modules.KnowledgeBase.ChatService>.Instance);
        var answer = await chat.AskAsync(paid.CustomerId, null, "Bảo hành bao lâu?", "q-1", None);
        Assert.True(answer.Succeeded && answer.Value!.Grounded, answer.Message);
        // An in-flight question (reservation not yet completed).
        var now = ctx.Clock.GetUtcNow();
        Assert.True((await uow.TryReserveUsageAsync(paid.CustomerId, DateOnly.FromDateTime(now.UtcDateTime), 100, options, now, now.AddMinutes(3), None)).Reserved);

        await PopulateRevisionBranchesAsync(ctx);
    }

    /// <summary>A second request: on-site amendment with a file, quotation document and revision, contract amendment.</summary>
    private static async Task PopulateRevisionBranchesAsync(InstallationWorkflowTestContext ctx)
    {
        ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var (survey, issued) = await QuotationTests.IssuedAsync(ctx, "Second customer");

        var report = await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.SurveyRequestId == survey.Request.Request.Id);
        var photo = await ctx.VerifiedFileAsync(survey.Technician.Id, SmartSolar.Modules.Files.FilePurposes.OnSiteSurveyEvidence);
        var amended = await ctx.OnSite().AmendAsync(new SmartSolar.Modules.OnSiteSurveys.AmendOnSiteSurveyCommand(survey.Technician.Id, survey.AppointmentId,
            "Re-measured the east roof", OnSiteSurveyTests.FullData(), [photo], report.Revision), None);
        Assert.True(amended.Succeeded, amended.Message);

        var quotations = ctx.Quotations();
        var pdf = await ctx.VerifiedFileAsync(survey.Sales.Id, SmartSolar.Modules.Files.FilePurposes.QuotationDocument, "pdf");
        Assert.True((await quotations.AttachDocumentAsync(survey.Sales.Id, issued.QuotationId, pdf, None)).Succeeded);
        var revision = await quotations.CreateRevisionAsync(survey.Sales.Id, issued.QuotationId, None);
        Assert.True(revision.Succeeded, revision.Message);
        Assert.True((await quotations.AcceptAsync(survey.Request.Customer.Id, issued.QuotationId, None)).Succeeded);

        var contracts = ctx.Contracts();
        var draft = (await contracts.CreateAsync(survey.Sales.Id, issued.QuotationId, "Terms", None)).Value!;
        var contract = (await contracts.IssueAsync(survey.Sales.Id, draft.ContractId, draft.Revision, None)).Value!;
        var amendment = await contracts.CreateAmendmentAsync(survey.Sales.Id, contract.ContractId, "Amended terms", None);
        Assert.True(amendment.Succeeded, amendment.Message);
    }
}
