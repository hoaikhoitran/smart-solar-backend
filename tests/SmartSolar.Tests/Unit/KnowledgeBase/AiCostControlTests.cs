using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// AI cost control (2026-11-17): exact USD arithmetic, the model registry and its cheapest default, the atomic admission (budget, quotas,
/// concurrency, idempotency) that runs BEFORE any provider call, exactly-once settlement, uncertain charges and their audited review, the
/// generation switch, and the ingestion budget. Every provider is a fake; nothing is billed.
/// </summary>
public sealed class AiCostControlTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig(InstallationWorkflowTestContext ctx)
    {
        public InstallationWorkflowTestContext Ctx { get; } = ctx;
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32), EmbeddingModel = "text-embedding-3-small" };
        public KnowledgeUnitOfWork Uow(AppDbContext? db = null) => new(db ?? Ctx.Db);
        public KnowledgeDocumentHandlers Documents() => new(Uow(), Ctx.Files(), Options, Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        public KnowledgeIngestionService Ingestion() => new(Uow(), new FileStore(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance);
        public ChatService Chat() => new(Uow(), Rag, Options, Ctx.Clock, NullLogger<ChatService>.Instance);
        public RagConfigurationService Config()
        {
            var rules = new RagConfigurationRules(Options);
            return new(Uow(), Options, rules, new EffectiveRagConfigurationProvider(Uow(), Options, rules, new RagConfigurationCache(TimeSpan.Zero), Ctx.Clock), Ctx.Clock,
                NullLogger<RagConfigurationService>.Instance);
        }

        public async Task<Guid> UserAsync(string name = "User") => (await Ctx.Scheduling.SeedTechnicianAsync($"{name} {Guid.NewGuid():N}", null, withRole: false)).Id;

        public async Task PublishAsync(Guid admin)
        {
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = (await Documents().RegisterAsync(admin, file, "Bảo hành", "warranty", null, None)).Value!;
            Assert.Equal(1, await Ingestion().RunDueAsync(10, None));
            var ready = (await Documents().GetAsync(registered.DocumentId, None)).Value!;
            Assert.True((await Documents().PublishAsync(ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, None)).Succeeded);
        }

        /// <summary>A billed answer: 1 200 prompt tokens (1 024 cached), 80 output, 12 query-embedding tokens, as gpt-6-luna reports them.</summary>
        public void BilledAnswers()
            => Rag.Answer = r => new RagAnswerResult(true, null, "Bảo hành 12 năm [1].", true,
                [new RagCitation(r.AllowedVersions[0].DocumentId, r.AllowedVersions[0].VersionId, "c:0", 1, null, 0.8, "Bảo hành 12 năm")], 1200, 80, "gpt-6-luna", 5, 50,
                EmbeddingTokens: 12, EmbeddingModel: "text-embedding-3-small", EmbeddingProvider: "openai", GenerationProvider: "openai", CachedPromptTokens: 1024);

        public async Task<IReadOnlyList<AiUsageLedgerEntry>> LedgerAsync() => await Ctx.Db.AiUsageLedger.AsNoTracking().ToListAsync();

        public async Task<AiBudgetPeriod> DayAsync()
            => await Ctx.Db.AiBudgetPeriods.AsNoTracking().SingleAsync(p => p.PeriodKind == AiBudgetPeriodKinds.Day);
    }

    // ---------------------------------------------------------------- arithmetic and registry

    [Fact]
    public void Costs_are_exact_and_the_reservation_is_an_upper_bound()
    {
        var luna = new AiPrice(0.10m, 0.01m, 0.50m);
        // 176 uncached × 0.10 + 1024 cached × 0.01 + 80 × 0.50 per 1M = 0.0000176 + 0.00001024 + 0.00004 = 0.00006784 (8 decimals: exact)
        Assert.Equal(0.00006784m, AiCostCalculator.Generation(luna, 1200, 1024, 80));
        Assert.Equal(0.00000024m, AiCostCalculator.Embedding(0.02m, 12));   // a query embedding: would be 0 at 6 decimals
        Assert.Equal(0.006m, AiCostCalculator.Embedding(0.02m, 300_000));
        // 1 000-character question: 335 tokens; input bound 335 + 2 000 + 1 334 + 1 200 = 4 869 → 0.0004869; output 300 × 0.5 = 0.00015;
        // embedding 335 × 0.02 = 0.0000067; total 0.0006436.
        var max = AiCostCalculator.MaxChat(luna, 0.02m, 1000, 2000, 300);
        Assert.Equal(0.0006436m, max);
        Assert.True(max > AiCostCalculator.Generation(luna, 4869, 0, 300));
        Assert.Equal(0.02000002m, AiCostCalculator.MaxIngestion(0.02m, 3_000_000));
    }

    [Fact]
    public void The_registry_defaults_to_the_cheapest_usable_model_and_refuses_unpriced_disabled_or_wrong_settings()
    {
        var rules = new RagConfigurationRules(new RagOptions());
        var luna = rules.CheapestUsableModel()!;
        Assert.Equal(("gpt-6-luna", false, "max_completion_tokens", "none"), (luna.Id, luna.SupportsTemperature, luna.TokenParameter, luna.DefaultReasoningEffort));
        Assert.Equal((0.10m, 0.01m, 0.50m), (luna.InputUsdPerMillion!.Value, luna.CachedInputUsdPerMillion!.Value, luna.OutputUsdPerMillion!.Value));
        var bootstrap = rules.Bootstrap(new RagOptions());
        Assert.Equal(("gpt-6-luna", (decimal?)null, "none", 300, 2000), (bootstrap.Values.GenerationModel, bootstrap.Values.Temperature, bootstrap.Values.ReasoningEffort,
            bootstrap.Values.MaxOutputTokens, bootstrap.Values.MaxContextTokens));
        Assert.Equal((30, 1, 3, 1000, 1.00m, 4.00m, 0.50m), (bootstrap.Values.UserRequestsPerDay, bootstrap.Values.UserConcurrency, bootstrap.Values.GlobalConcurrency,
            bootstrap.Values.MaxQuestionCharacters, bootstrap.Values.DailyBudgetUsd, bootstrap.Values.MonthlyBudgetUsd, bootstrap.Values.MonthlyIngestionBudgetUsd));

        var valid = bootstrap.Values;
        Assert.Contains("generationModel", rules.Validate(valid with { GenerationModel = "gpt-4.1-mini" }).Keys);   // disabled: no verified price
        Assert.Contains("generationModel", rules.Validate(valid with { GenerationModel = "gpt-9-imaginary" }).Keys);
        Assert.Contains("reasoningEffort", rules.Validate(valid with { ReasoningEffort = "minimal" }).Keys);         // not a gpt-6-luna value
        Assert.Contains("temperature", rules.Validate(valid with { Temperature = 0.2m }).Keys);
        Assert.Empty(rules.Validate(valid with { ReasoningEffort = "low" }));
        // A local model configured without a provider (as the current VPS registry) is local and free, so it stays usable.
        var local = new RagConfigurationRules(new RagOptions { GenerationModels = [new() { Id = "qwen3:1.7b", TokenParameter = RagTokenParameters.MaxTokens }] });
        Assert.True(local.FindModel("qwen3:1.7b") is { Usable: true, Billed: false });
        Assert.Throws<InvalidOperationException>(() => new RagConfigurationRules(new RagOptions
        {
            GenerationModels = [new() { Id = "m", ReasoningEfforts = ["turbo"] }],
        }));
    }

    [Fact]
    public async Task Cost_controls_are_versioned_audited_and_bounded_by_hard_ceilings()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        var config = rig.Config();
        Assert.Equal(RagConfigurationErrorCodes.ValidationFailed, (await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null,
            null, null, null, null, null, DailyBudgetUsd: 50m), 0, null, None)).ErrorCode);   // ceiling 10 USD/day
        Assert.Equal(RagConfigurationErrorCodes.ValidationFailed, (await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null,
            null, null, null, null, null, DailyBudgetUsd: 5m, MonthlyBudgetUsd: 4m), 0, null, None)).ErrorCode);   // day > month
        Assert.Equal(RagConfigurationErrorCodes.ValidationFailed, (await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null,
            null, null, null, null, null, UserConcurrency: 0), 0, null, None)).ErrorCode);    // no "unlimited" / zero values
        Assert.Equal(RagConfigurationErrorCodes.StaleRevision, (await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null,
            null, null, null, null, null, GenerationEnabled: false), 7, null, None)).ErrorCode);

        var off = await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null,
            GenerationEnabled: false, DailyBudgetUsd: 0.50m), 0, "Kill switch drill", None);
        Assert.True(off.Succeeded, off.Message);
        Assert.Equal((1, false, 0.50m, admin), (off.Value!.Active.Version, off.Value.Active.GenerationEnabled, off.Value.Active.DailyBudgetUsd, off.Value.Active.ActivatedBy!.Value));
        var back = await config.RollbackAsync(admin, 1, None);
        Assert.Equal(RagConfigurationErrorCodes.InvalidState, back.ErrorCode);   // 1 is active
        var on = await config.SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null,
            GenerationEnabled: true), 1, null, None);
        Assert.True(on.Value!.Active.GenerationEnabled);
        Assert.Equal(2, (await ctx.Db.Set<RagConfiguration>().CountAsync()));
    }

    // ---------------------------------------------------------------- chat admission and settlement

    [Fact]
    public async Task A_paid_question_is_reserved_before_the_call_and_settled_exactly_from_the_reported_usage()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        await rig.PublishAsync(admin);
        rig.BilledAnswers();
        var user = await rig.UserAsync();

        var answer = await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", "m-1", None, KnowledgeAudience.Staff, userRole: "Technician");
        Assert.True(answer.Succeeded, answer.Message);
        // The request carried the registry's reasoning effort and the admin timeout; gpt-6-luna is the bootstrap default but bootstrap sends no model.
        Assert.Null(rig.Rag.LastAnswerRequest!.GenerationModel);
        var chat = (await rig.LedgerAsync()).Single(e => e.Operation == AiUsageOperations.Chat);
        Assert.Equal((AiUsageStatuses.Settled, AiUsageOutcomes.Answered, "Technician"), (chat.Status, chat.Outcome, chat.UserRole));
        Assert.Equal((1200L, 1024L, 80L, 12L), (chat.InputTokens, chat.CachedInputTokens, chat.OutputTokens, chat.EmbeddingTokens));
        Assert.Equal((0.00006784m, 0.00000024m, 0.00006808m), (chat.GenerationCostUsd, chat.EmbeddingCostUsd, chat.CostUsd));
        Assert.True(chat.ReservedUsd >= chat.CostUsd);
        Assert.Equal(answer.Value!.Answer.MessageId, chat.ChatMessageId);
        var day = await rig.DayAsync();
        Assert.Equal((0m, 0.00006808m, 0m, 1), (day.ReservedUsd, day.SettledUsd, day.UncertainUsd, day.Requests));
        Assert.Empty(await ctx.Db.RagUsageReservations.ToListAsync());

        // The retried question (same client message id) returns the stored answer: no second paid call, no second ledger row.
        var calls = rig.Rag.AnswerCalls;
        Assert.True((await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", "m-1", None, KnowledgeAudience.Staff)).Succeeded);
        Assert.Equal(calls, rig.Rag.AnswerCalls);
        Assert.Single((await rig.LedgerAsync()).Where(e => e.Operation == AiUsageOperations.Chat));
    }

    [Fact]
    public async Task An_exhausted_budget_or_the_switch_refuses_paid_questions_before_any_provider_call_while_the_advisor_keeps_working()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        await rig.PublishAsync(admin);
        rig.BilledAnswers();
        var user = await rig.UserAsync();
        // A budget smaller than one question's maximum.
        Assert.True((await rig.Config().SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null,
            DailyBudgetUsd: 0m), 0, null, None)).Succeeded);

        var refused = await rig.Chat().AskAsync(user, null, "Inverter bảo hành mấy năm?", null, None, KnowledgeAudience.Staff);
        Assert.Equal(AiCostErrorCodes.BudgetExceeded, refused.ErrorCode);
        Assert.Equal(429, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Contains("retryAfterSeconds", System.Text.Json.JsonSerializer.Serialize(refused.Details));
        Assert.Equal(0, rig.Rag.AnswerCalls);
        Assert.Equal(1, (await rig.DayAsync()).Blocked);
        Assert.Empty((await rig.LedgerAsync()).Where(e => e.Operation == AiUsageOperations.Chat));

        // Generation switched off: refused (503) without a call; deterministic advice still answers (no budget needed).
        Assert.True((await rig.Config().SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null,
            GenerationEnabled: false, DailyBudgetUsd: 1m), 1, null, None)).Succeeded);
        var off = await rig.Chat().AskAsync(user, null, "Inverter bảo hành mấy năm?", null, None, KnowledgeAudience.Staff);
        Assert.Equal(AiCostErrorCodes.GenerationDisabled, off.ErrorCode);
        Assert.Equal(503, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(off.ErrorCode!));
        var advisor = new SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorService(new SolarAdvisorDataSource(ctx.Db), null,
            new SmartSolar.Modules.KnowledgeBase.Advisor.SolarYieldCache(), new SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorOptions(), ctx.Clock);
        var advice = await new ChatService(rig.Uow(), rig.Rag, rig.Options, ctx.Clock, NullLogger<ChatService>.Instance, advisor)
            .AskAsync(user, null, "Nhà tôi có mái 50 m², nên lắp bao nhiêu tấm pin?", null, None, KnowledgeAudience.Staff);
        Assert.True(advice.Succeeded, advice.Message);
        Assert.Equal(0, rig.Rag.AnswerCalls);
        var released = Assert.Single((await rig.LedgerAsync()).Where(e => e.Operation == AiUsageOperations.Chat));
        Assert.Equal((AiUsageStatuses.Released, 0m, 0m), (released.Status, released.ReservedUsd, released.CostUsd));
    }

    [Fact]
    public async Task A_timeout_stays_an_uncertain_charge_until_an_admin_reviews_it_and_a_refused_call_costs_only_its_embedding()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        await rig.PublishAsync(admin);
        var user = await rig.UserAsync();

        // The provider may have processed the call (timeout at the RAG service): the reservation is kept as the charge.
        rig.Rag.Answer = _ => new RagAnswerResult(false, "timeout", null, false, [], 0, 0, null, 0, 0, EmbeddingTokens: 12, EmbeddingProvider: "openai", Billing: "uncertain");
        Assert.Equal(KnowledgeErrorCodes.Timeout, (await rig.Chat().AskAsync(user, null, "Câu hỏi?", null, None, KnowledgeAudience.Staff)).ErrorCode);
        var uncertain = Assert.Single((await rig.LedgerAsync()).Where(e => e.Operation == AiUsageOperations.Chat));
        Assert.Equal((AiUsageStatuses.Uncertain, AiUsageOutcomes.Timeout), (uncertain.Status, uncertain.Outcome));
        Assert.Equal(uncertain.ReservedUsd, uncertain.CostUsd);
        Assert.Equal(uncertain.ReservedUsd, (await rig.DayAsync()).UncertainUsd);

        // The provider refused with 429 (not billed): only the question embedding is settled.
        rig.Rag.Answer = _ => new RagAnswerResult(false, "provider rate limited", null, false, [], 0, 0, null, 0, 0, EmbeddingTokens: 12, EmbeddingProvider: "openai", Billing: "none");
        await rig.Chat().AskAsync(user, null, "Câu hỏi khác?", null, None, KnowledgeAudience.Staff);
        var limited = (await rig.LedgerAsync()).Single(e => e.Outcome == AiUsageOutcomes.ProviderRateLimited);
        Assert.Equal((AiUsageStatuses.Settled, 0.00000024m), (limited.Status, limited.CostUsd));

        // Review against the provider's billing: settled at the confirmed cost, audited.
        var dashboard = new AiUsageDashboardService(new AiUsageQueries(ctx.Db), rig.Uow(), new EffectiveRagConfigurationProvider(rig.Uow(), rig.Options,
            new RagConfigurationRules(rig.Options), new RagConfigurationCache(TimeSpan.Zero), ctx.Clock), rig.Config(), rig.Options, ctx.Clock);
        Assert.Equal(SmartSolar.Modules.Common.Results.CommonErrorCodes.ValidationFailed, (await dashboard.ReviewAsync(admin, uncertain.Id, -1m, "x", None)).ErrorCode);
        Assert.True((await dashboard.ReviewAsync(admin, uncertain.Id, 0.0002m, "Matched OpenAI usage export 2026-11-17", None)).Succeeded);
        Assert.Equal(AiCostErrorCodes.UsageNotFound, (await dashboard.ReviewAsync(admin, uncertain.Id, 0m, "again", None)).ErrorCode);
        var reviewed = await ctx.Db.AiUsageLedger.AsNoTracking().SingleAsync(e => e.Id == uncertain.Id);
        Assert.Equal((AiUsageStatuses.Settled, AiUsageOutcomes.Reviewed, 0.0002m, admin), (reviewed.Status, reviewed.Outcome, reviewed.CostUsd, reviewed.ReviewedByUserId!.Value));
        var day = await rig.DayAsync();
        Assert.Equal((0m, 0.00020024m), (day.UncertainUsd, day.SettledUsd));
        var summary = (await dashboard.SummaryAsync(null, null, None)).Value!;
        Assert.Equal((2, 0.00020024m), (summary.Requests, summary.Today.SettledUsd));
    }

    [Fact]
    public async Task A_question_in_flight_cannot_be_admitted_twice_and_crashed_reservations_become_uncertain()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        await rig.PublishAsync(admin);
        var user = await rig.UserAsync();
        var now = ctx.Clock.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        AiAdmissionRequest Request(Guid u, string? client, decimal usd) => new(u, "Customer", date, 100, usd, true, client, 0, 30, 2, 1, 1m, 4m, now, now.AddMinutes(2));

        // In flight on another replica: the same client message id is refused, no provider call.
        var first = await rig.Uow().TryAdmitAsync(Request(user, "dup-1", 0.0005m), rig.Options, None);
        Assert.True(first.Admitted);
        var duplicate = await rig.Chat().AskAsync(user, null, "Câu hỏi?", "dup-1", None, KnowledgeAudience.Staff);
        Assert.Equal(AiCostErrorCodes.RequestInProgress, duplicate.ErrorCode);
        Assert.Equal(0, rig.Rag.AnswerCalls);

        // Global concurrency 1: another user's billable question waits.
        var other = await rig.UserAsync();
        Assert.Equal(AiCostErrorCodes.GlobalBusy, (await rig.Uow().TryAdmitAsync(Request(other, null, 0.0005m), rig.Options, None)).Limit);

        // The first one never settles (crashed worker): after its lease it is an uncertain charge, never silently free.
        ctx.Clock.Now = now.AddMinutes(3);
        Assert.Equal(1, await rig.Uow().ExpireAiReservationsAsync(ctx.Clock.GetUtcNow(), 10, None));
        var expired = Assert.Single((await rig.LedgerAsync()).Where(e => e.Operation == AiUsageOperations.Chat));
        Assert.Equal((AiUsageStatuses.Uncertain, AiUsageOutcomes.Expired, 0.0005m), (expired.Status, expired.Outcome, expired.CostUsd));
        var day = await rig.DayAsync();
        Assert.Equal((0m, 0.0005m), (day.ReservedUsd, day.UncertainUsd));
        // A late settlement of the expired reservation changes nothing (exactly once).
        Assert.True(await rig.Uow().SettleChatAsync(first.ReservationId!.Value, 1, 1, new AiChatSettlement(AiUsageOutcomes.Answered, false, null, null, null, 0, 0, null, null,
            0, 0, 0, 0, 0.0001m, true, 1, ctx.Clock.GetUtcNow()), None));
        Assert.Equal(AiUsageStatuses.Uncertain, (await ctx.Db.AiUsageLedger.AsNoTracking().SingleAsync(e => e.Id == first.ReservationId)).Status);
    }

    [Fact]
    public async Task Questions_over_the_configured_length_are_refused_before_anything_is_reserved()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var user = await rig.UserAsync();
        var tooLong = await rig.Chat().AskAsync(user, null, new string('a', 1001), null, None, KnowledgeAudience.Staff);
        Assert.Equal(KnowledgeErrorCodes.MessageTooLong, tooLong.ErrorCode);
        Assert.Equal(400, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(tooLong.ErrorCode!));
        Assert.Empty(await ctx.Db.RagUsageReservations.ToListAsync());
    }

    // ---------------------------------------------------------------- ingestion budget

    [Fact]
    public async Task Ingestion_reserves_its_maximum_embedding_cost_settles_the_reported_tokens_and_defers_without_budget()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.UserAsync("Admin");
        rig.Rag.Ingest = (r, _) => Task.FromResult(new RagIngestResult(true, "READY", 3, 12, 4000, "text-embedding-3-small", 1024, null, r.ConfigVersion,
            r.ChunkCharacters, r.ChunkOverlap, EmbeddingTokens: 1000, EmbeddingProvider: "openai"));
        await rig.PublishAsync(admin);
        var ingest = Assert.Single(await rig.LedgerAsync());
        Assert.Equal((AiUsageOperations.Ingestion, AiUsageStatuses.Settled, 1000L, 0.00002m), (ingest.Operation, ingest.Status, ingest.EmbeddingTokens, ingest.CostUsd));
        // The reservation was taken before the call; a report above it (this fake's tiny file "holds" 1 000 tokens) is still settled at the reported cost.
        Assert.True(ingest.ReservedUsd > 0);

        // No ingestion budget left: the attempt is deferred and the RAG service is never called.
        Assert.True((await rig.Config().SetCostControlsAsync(admin, new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null,
            MonthlyIngestionBudgetUsd: 0m), 0, null, None)).Succeeded);
        var calls = rig.Rag.IngestCalls;
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        Assert.True((await rig.Documents().RegisterAsync(admin, file, "Bảo trì", "maintenance", null, None)).Succeeded);
        await rig.Ingestion().RunDueAsync(10, None);
        Assert.Equal(calls, rig.Rag.IngestCalls);
        var job = (await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().ToListAsync()).OrderBy(j => j.CreatedAt).Last();
        Assert.StartsWith("AI_BUDGET_EXCEEDED", job.LastError);
        var reindex = await rig.Config().ReindexStaleAsync(admin, None);
        Assert.True(reindex.Succeeded || reindex.ErrorCode == AiCostErrorCodes.BudgetExceeded);
    }
}
