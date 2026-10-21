using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// Admin RAG runtime configuration: validation against the model registry and bounds, revision-checked drafts, atomic activation,
/// rollback as a new version, the effective configuration used by chat and ingestion (job snapshots), reindex of stale versions,
/// and the published-version safety of a failed reindex.
/// </summary>
public sealed class RagConfigurationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig
    {
        public Rig(InstallationWorkflowTestContext ctx, TimeSpan? cacheTtl = null)
        {
            Ctx = ctx;
            Options.GenerationModels =
            [
                .. RagConfigurationRules.DefaultModels,
                new RagGenerationModelOptions
                {
                    Id = "o4-mini", DisplayName = "o4 mini", SupportsTemperature = false, TokenParameter = RagTokenParameters.MaxCompletionTokens, MaxOutputTokensLimit = 1000,
                },
            ];
            Rules = new RagConfigurationRules(Options);
            Cache = new RagConfigurationCache(cacheTtl ?? TimeSpan.Zero);
        }

        public InstallationWorkflowTestContext Ctx { get; }
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32) };
        public RagConfigurationRules Rules { get; }
        public RagConfigurationCache Cache { get; }

        public IRagConfigurationProvider Provider(AppDbContext? db = null)
            => new EffectiveRagConfigurationProvider(new KnowledgeUnitOfWork(db ?? Ctx.Db), Options, Rules, Cache, Ctx.Clock);
        public RagConfigurationService Config(AppDbContext? db = null)
            => new(new KnowledgeUnitOfWork(db ?? Ctx.Db), Options, Rules, Provider(db), Ctx.Clock, NullLogger<RagConfigurationService>.Instance);
        public KnowledgeDocumentHandlers Documents() => new(new KnowledgeUnitOfWork(Ctx.Db), Ctx.Files(), Options, Provider(), Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        public KnowledgeIngestionService Ingestion() => new(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), Rag, Options, Provider(), Ctx.Clock,
            NullLogger<KnowledgeIngestionService>.Instance);
        public ChatService Chat() => new(new KnowledgeUnitOfWork(Ctx.Db), Rag, Options, Provider(), Ctx.Clock, NullLogger<ChatService>.Instance);

        public async Task<Guid> AdminAsync() => (await Ctx.Scheduling.SeedTechnicianAsync($"Admin {Guid.NewGuid():N}", null, withRole: false)).Id;

        /// <summary>Saves a draft with the given changes and activates it.</summary>
        public async Task<RagActivationView> ActivateAsync(Guid admin, RagConfigurationInput input)
        {
            var current = (await Config().GetAsync(None)).Draft?.Revision;
            var draft = await Config().SaveDraftAsync(admin, input, current, None);
            Assert.True(draft.Succeeded, $"{draft.ErrorCode}: {draft.Message} {Errors(draft)}");
            var activated = await Config().ActivateDraftAsync(admin, draft.Value!.Revision, None);
            Assert.True(activated.Succeeded, activated.Message);
            return activated.Value!;
        }

        public async Task<KnowledgeDocumentView> RegisteredAsync(Guid admin, string title = "Bảo hành")
        {
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = await Documents().RegisterAsync(admin, file, title, "WARRANTY", null, None);
            Assert.True(registered.Succeeded, registered.Message);
            return registered.Value!;
        }

        public async Task<KnowledgeDocumentView> PublishedAsync(Guid admin, string title = "Bảo hành")
        {
            var doc = await RegisteredAsync(admin, title);
            await Ingestion().RunDueAsync(10, None);
            var ready = (await Documents().GetAsync(doc.DocumentId, None)).Value!;
            var published = await Documents().PublishAsync(ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, None);
            Assert.True(published.Succeeded, published.Message);
            return published.Value!;
        }
    }

    private static RagConfigurationInput Input(string? model = null, bool temperatureSpecified = false, decimal? temperature = null, int? maxOutput = null,
        int? chunk = null, int? overlap = null, int? topK = null, decimal? minScore = null, int? context = null, int? history = null, string? prompt = null, string? notes = null)
        => new(model, temperatureSpecified, temperature, maxOutput, chunk, overlap, topK, minScore, context, history, prompt, notes);

    private static string Errors<T>(SmartSolar.Modules.Common.Results.OperationResult<T> r)
        => r.Details is Dictionary<string, string[]> d ? string.Join("; ", d.Select(kv => $"{kv.Key}: {string.Join(",", kv.Value)}")) : "";

    private static IReadOnlyDictionary<string, string[]> FieldErrors<T>(SmartSolar.Modules.Common.Results.OperationResult<T> r)
    {
        Assert.Equal(RagConfigurationErrorCodes.ValidationFailed, r.ErrorCode);
        return Assert.IsType<Dictionary<string, string[]>>(r.Details);
    }

    [Fact]
    public async Task Without_an_active_row_the_bootstrap_values_are_effective()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var state = await rig.Config().GetAsync(None);
        Assert.True(state.Bootstrap);
        Assert.Null(state.Active);
        Assert.Null(state.Draft);
        Assert.Equal(0, state.Effective.Version);
        Assert.Equal("gpt-4.1-mini", state.Effective.GenerationModel);
        Assert.Equal(2400, state.Effective.ChunkCharacters);
        var effective = await rig.Provider().GetAsync(None);
        Assert.True(effective.Bootstrap);
        Assert.Equal(rig.Options.MaxContextTokens, effective.Values.MaxContextTokens);
    }

    [Fact]
    public async Task Drafts_are_validated_against_the_model_registry_and_bounds()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var config = rig.Config();

        Assert.Contains("generationModel", FieldErrors(await config.SaveDraftAsync(admin, Input(model: "gpt-5-unknown"), null, None)).Keys);
        Assert.Contains("chunkCharacters", FieldErrors(await config.SaveDraftAsync(admin, Input(chunk: 399, overlap: 0), null, None)).Keys);
        Assert.Contains("chunkCharacters", FieldErrors(await config.SaveDraftAsync(admin, Input(chunk: 8001), null, None)).Keys);
        // Overlap: at most half the chunk and always smaller than it.
        Assert.Contains("chunkOverlap", FieldErrors(await config.SaveDraftAsync(admin, Input(chunk: 1000, overlap: 1000), null, None)).Keys);
        Assert.Contains("chunkOverlap", FieldErrors(await config.SaveDraftAsync(admin, Input(chunk: 1000, overlap: 501), null, None)).Keys);
        Assert.Contains("chunkOverlap", FieldErrors(await config.SaveDraftAsync(admin, Input(overlap: -1), null, None)).Keys);
        Assert.Contains("topK", FieldErrors(await config.SaveDraftAsync(admin, Input(topK: 11), null, None)).Keys);
        Assert.Contains("minScore", FieldErrors(await config.SaveDraftAsync(admin, Input(minScore: 1.5m), null, None)).Keys);
        Assert.Contains("maxContextTokens", FieldErrors(await config.SaveDraftAsync(admin, Input(context: 499), null, None)).Keys);
        Assert.Contains("maxOutputTokens", FieldErrors(await config.SaveDraftAsync(admin, Input(maxOutput: 1501), null, None)).Keys);
        Assert.Contains("maxHistoryMessages", FieldErrors(await config.SaveDraftAsync(admin, Input(history: 21), null, None)).Keys);
        Assert.Contains("temperature", FieldErrors(await config.SaveDraftAsync(admin, Input(temperatureSpecified: true, temperature: 1.2m), null, None)).Keys);
        Assert.Contains("promptVersion", FieldErrors(await config.SaveDraftAsync(admin, Input(prompt: "v2"), null, None)).Keys);
        // The model's own output limit applies (o4-mini: 1000).
        Assert.Contains("maxOutputTokens", FieldErrors(await config.SaveDraftAsync(admin, Input(model: "o4-mini", maxOutput: 1200), null, None)).Keys);
        Assert.Null((await config.GetAsync(None)).Draft);
        Assert.Equal(0, await ctx.Db.Set<RagConfiguration>().CountAsync());

        var ok = await config.SaveDraftAsync(admin, Input(chunk: 1000, overlap: 500, topK: 10, minScore: 0.35m, notes: "Tuning"), null, None);
        Assert.True(ok.Succeeded, Errors(ok));
        Assert.Equal(RagConfigurationStatuses.Draft, ok.Value!.Status);
        Assert.Equal(1, ok.Value.Version);
        Assert.Null(ok.Value.BasedOnVersion);   // created from the bootstrap values
        Assert.Equal("gpt-4.1-mini", ok.Value.GenerationModel);
    }

    [Fact]
    public async Task Temperature_is_rejected_for_models_without_temperature_support()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();

        var rejected = await rig.Config().SaveDraftAsync(admin, Input(model: "o4-mini", temperatureSpecified: true, temperature: 0.2m, maxOutput: 800), null, None);
        Assert.Contains("temperature", FieldErrors(rejected).Keys);

        // Not re-specified: the inherited temperature is dropped for such a model; the token parameter follows the model.
        var saved = await rig.Config().SaveDraftAsync(admin, Input(model: "o4-mini", maxOutput: 800), null, None);
        Assert.True(saved.Succeeded, Errors(saved));
        Assert.Null(saved.Value!.Temperature);
        Assert.False(saved.Value.SupportsTemperature);
        Assert.Equal(RagTokenParameters.MaxCompletionTokens, saved.Value.TokenParameter);
    }

    [Fact]
    public async Task Draft_updates_and_activation_require_the_current_revision()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var draft = (await rig.Config().SaveDraftAsync(admin, Input(topK: 4), null, None)).Value!;

        // An existing draft cannot be overwritten blindly or with a stale revision.
        Assert.Equal(RagConfigurationErrorCodes.StaleRevision, (await rig.Config().SaveDraftAsync(admin, Input(topK: 5), null, None)).ErrorCode);
        Assert.Equal(RagConfigurationErrorCodes.StaleRevision, (await rig.Config().SaveDraftAsync(admin, Input(topK: 5), draft.Revision + 1, None)).ErrorCode);
        var updated = await rig.Config().SaveDraftAsync(admin, Input(topK: 5), draft.Revision, None);
        Assert.True(updated.Succeeded);
        Assert.Equal(draft.Revision + 1, updated.Value!.Revision);
        Assert.Equal(draft.Version, updated.Value.Version);
        Assert.Equal(RagConfigurationErrorCodes.StaleRevision, (await rig.Config().ActivateDraftAsync(admin, draft.Revision, None)).ErrorCode);
        Assert.Null((await rig.Config().GetAsync(None)).Active);

        // Deleting the draft also checks the revision.
        Assert.Equal(RagConfigurationErrorCodes.StaleRevision, (await rig.Config().DeleteDraftAsync(draft.Revision, None)).ErrorCode);
        Assert.True((await rig.Config().DeleteDraftAsync(updated.Value.Revision, None)).Succeeded);
        Assert.Equal(RagConfigurationErrorCodes.DraftNotFound, (await rig.Config().DeleteDraftAsync(null, None)).ErrorCode);
        Assert.Equal(RagConfigurationErrorCodes.DraftNotFound, (await rig.Config().SaveDraftAsync(admin, Input(topK: 5), updated.Value.Revision, None)).ErrorCode);
    }

    [Fact]
    public async Task Activation_supersedes_the_previous_version_and_rollback_creates_a_new_active_version()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();

        var first = await rig.ActivateAsync(admin, Input(topK: 3, minScore: 0.3m));
        Assert.Equal(1, first.Active.Version);
        Assert.Null(first.SupersededVersion);
        Assert.Equal(admin, first.Active.ActivatedBy);

        var second = await rig.ActivateAsync(admin, Input(topK: 8, model: "gpt-4.1"));
        Assert.Equal(2, second.Active.Version);
        Assert.Equal(1, second.SupersededVersion);
        Assert.Equal(1, second.Active.BasedOnVersion);
        Assert.Equal(8, second.Active.TopK);
        Assert.Equal(0.3m, second.Active.MinScore);   // not re-specified: inherited from the active version

        var rows = await ctx.Db.Set<RagConfiguration>().AsNoTracking().OrderBy(c => c.Version).ToListAsync();
        Assert.Equal([RagConfigurationStatuses.Superseded, RagConfigurationStatuses.Active], rows.Select(r => r.Status));

        Assert.Equal(RagConfigurationErrorCodes.InvalidState, (await rig.Config().RollbackAsync(admin, 2, None)).ErrorCode);
        Assert.Equal(RagConfigurationErrorCodes.VersionNotFound, (await rig.Config().RollbackAsync(admin, 42, None)).ErrorCode);
        // A pending draft does not block a rollback (and survives it).
        var draft = (await rig.Config().SaveDraftAsync(admin, Input(topK: 9), null, None)).Value!;
        var rolledBack = await rig.Config().RollbackAsync(admin, 1, None);
        Assert.True(rolledBack.Succeeded, rolledBack.Message);
        Assert.Equal(draft.Version + 1, rolledBack.Value!.Active.Version);
        Assert.Equal(1, rolledBack.Value.Active.BasedOnVersion);
        Assert.Equal(2, rolledBack.Value.SupersededVersion);
        Assert.Equal(3, rolledBack.Value.Active.TopK);
        Assert.Equal(0.3m, rolledBack.Value.Active.MinScore);
        Assert.Equal("gpt-4.1-mini", rolledBack.Value.Active.GenerationModel);

        var state = await rig.Config().GetAsync(None);
        Assert.False(state.Bootstrap);
        Assert.Equal(rolledBack.Value.Active.Version, state.Effective.Version);
        Assert.Equal(draft.Version, state.Draft!.Version);
        // Version 1 itself is untouched history.
        var history = await rig.Config().HistoryAsync(1, 10, None);
        Assert.Equal(4, history.Total);
        Assert.Equal([4, 3, 2, 1], history.Items.Select(i => i.Version));
        Assert.Equal(RagConfigurationStatuses.Superseded, history.Items.Single(i => i.Version == 1).Status);
        Assert.Single(history.Items, i => i.Status == RagConfigurationStatuses.Active);
    }

    [Fact]
    public async Task The_active_configuration_persists_for_a_new_context_and_survives_a_new_service_instance()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.ActivateAsync(admin, Input(topK: 2, chunk: 1200, overlap: 100));

        ctx.Db.ChangeTracker.Clear();
        var fresh = new Rig(ctx);
        var effective = await fresh.Provider().GetAsync(None);
        Assert.False(effective.Bootstrap);
        Assert.Equal(1, effective.Version);
        Assert.Equal(2, effective.Values.TopK);
        Assert.Equal(1200, effective.Values.ChunkCharacters);
    }

    [Fact]
    public async Task The_effective_configuration_is_cached_briefly_and_invalidated_by_activation()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx, TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Cache.Ttl);   // clamped
        var admin = await rig.AdminAsync();
        Assert.True((await rig.Provider().GetAsync(None)).Bootstrap);

        // A write that bypasses the service (another instance) is seen only after the TTL…
        ctx.Db.Set<RagConfiguration>().Add(new RagConfiguration
        {
            Id = Guid.NewGuid(), Version = 1, Status = RagConfigurationStatuses.Active, GenerationModel = "gpt-4.1", Temperature = 0.2m, MaxOutputTokens = 500,
            ChunkCharacters = 1000, ChunkOverlap = 100, TopK = 4, MinScore = 0.25m, MaxContextTokens = 2000, MaxHistoryMessages = 4, PromptVersion = "v1",
            CreatedByUserId = admin, CreatedAt = ctx.Clock.GetUtcNow(), UpdatedAt = ctx.Clock.GetUtcNow(), ActivatedByUserId = admin, ActivatedAt = ctx.Clock.GetUtcNow(),
            Revision = 1,
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.True((await rig.Provider().GetAsync(None)).Bootstrap);
        ctx.Clock.Now += TimeSpan.FromSeconds(16);
        Assert.Equal(1, (await rig.Provider().GetAsync(None)).Version);

        // …while an activation through the service invalidates this instance's cache at once.
        await rig.ActivateAsync(admin, Input(topK: 7));
        Assert.Equal(7, (await rig.Provider().GetAsync(None)).Values.TopK);
    }

    [Fact]
    public async Task Chat_and_ingestion_send_the_active_values_to_the_rag_service()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.ActivateAsync(admin, Input(model: "o4-mini", maxOutput: 900, chunk: 1500, overlap: 200, topK: 3, minScore: 0.42m, context: 2500, history: 1));
        await rig.PublishedAsync(admin);

        var ingest = rig.Rag.LastIngestRequest!;
        Assert.Equal((1, 1500, 200), (ingest.ConfigVersion, ingest.ChunkCharacters!.Value, ingest.ChunkOverlap!.Value));
        var version = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal((1, 1500, 200), (version.IndexedConfigVersion!.Value, version.IndexedChunkCharacters!.Value, version.IndexedChunkOverlap!.Value));
        Assert.Equal("READY", version.LastIngestStatus);

        var first = await rig.Chat().AskAsync(admin, null, "Câu 1", "a", None);
        Assert.True(first.Succeeded && first.Value!.Grounded, first.Message);
        await rig.Chat().AskAsync(admin, first.Value.SessionId, "Câu 2", "b", None);
        var answer = rig.Rag.LastAnswerRequest!;
        Assert.Equal(1, answer.ConfigVersion);
        Assert.Equal("o4-mini", answer.GenerationModel);
        Assert.Null(answer.Temperature);
        Assert.Equal(RagTokenParameters.MaxCompletionTokens, answer.TokenParameter);
        Assert.Equal(900, answer.MaxOutputTokens);
        Assert.Equal(2500, answer.MaxContextTokens);
        Assert.Equal(3, answer.TopK);
        Assert.Equal(0.42m, answer.MinScore);
        Assert.Equal("v1", answer.PromptVersion);
        Assert.Single(answer.History);   // maxHistoryMessages = 1

        // Bootstrap (no active row): the retrieval/prompt values are sent, but model, temperature and token parameter are left to the
        // RAG service's environment defaults (RAG_LLM_MODEL / RAG_LLM_TEMPERATURE) — they are not even present in the payload.
        await using var ctx2 = InstallationWorkflowTestContext.Create();
        var bootstrap = new Rig(ctx2);
        var admin2 = await bootstrap.AdminAsync();
        await bootstrap.PublishedAsync(admin2);
        await bootstrap.Chat().AskAsync(admin2, null, "Câu 1", null, None);
        var b = bootstrap.Rag.LastAnswerRequest!;
        Assert.Equal((0, 0.2m, "v1", bootstrap.Options.TopK, bootstrap.Options.MaxOutputTokens), (b.ConfigVersion, b.MinScore!.Value, b.PromptVersion!, b.TopK, b.MaxOutputTokens));
        Assert.Null(b.GenerationModel);
        Assert.Null(b.Temperature);
        Assert.Null(b.TokenParameter);
        var wire = SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient.AnswerPayload(b).ToJsonString();
        Assert.DoesNotContain("generationModel", wire);
        Assert.DoesNotContain("temperature", wire);
        Assert.DoesNotContain("tokenParameter", wire);
        Assert.Contains("\"minScore\":0.2", wire);
        // Once an admin configuration is ACTIVE it overrides the service defaults again.
        await bootstrap.ActivateAsync(admin2, Input(model: "gpt-4.1", temperatureSpecified: true, temperature: 0.3m));
        await bootstrap.Chat().AskAsync(admin2, null, "Câu 2", null, None);
        var active = bootstrap.Rag.LastAnswerRequest!;
        Assert.Equal(("gpt-4.1", 0.3m, RagTokenParameters.MaxTokens), (active.GenerationModel!, active.Temperature!.Value, active.TokenParameter!));
        Assert.Equal((0, 2400, 300), (bootstrap.Rag.LastIngestRequest!.ConfigVersion, bootstrap.Rag.LastIngestRequest.ChunkCharacters!.Value,
            bootstrap.Rag.LastIngestRequest.ChunkOverlap!.Value));
    }

    [Fact]
    public async Task An_in_flight_job_keeps_the_snapshot_it_was_queued_with()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.RegisteredAsync(admin);   // queued under bootstrap (v0, 2400/300)
        var job = await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync();
        Assert.Equal((0, 2400, 300), (job.ConfigVersion!.Value, job.ChunkCharacters!.Value, job.ChunkOverlap!.Value));

        var activation = await rig.ActivateAsync(admin, Input(chunk: 1000, overlap: 100));
        Assert.True(activation.ChunkSettingsChanged);
        await rig.Ingestion().RunDueAsync(10, None);

        var sent = rig.Rag.LastIngestRequest!;
        Assert.Equal((0, 2400, 300), (sent.ConfigVersion, sent.ChunkCharacters!.Value, sent.ChunkOverlap!.Value));
        var version = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(0, version.IndexedConfigVersion);
        Assert.Equal(2400, version.IndexedChunkCharacters);
    }

    [Fact]
    public async Task Only_what_the_service_echoes_is_recorded_as_indexed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        // An older RAG service that ignores the chunk fields echoes nothing back.
        rig.Rag.Ingest = (_, _) => Task.FromResult(new RagIngestResult(true, "READY", 1, 5, 100, "hashing-dev", 384, null));
        await rig.RegisteredAsync(admin);
        await rig.Ingestion().RunDueAsync(10, None);
        var version = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.ReadyForReview, version.Status);
        Assert.Null(version.IndexedChunkCharacters);
        Assert.Equal(1, (await rig.Config().ReindexStaleAsync(admin, None)).Value!.Queued);
    }

    [Fact]
    public async Task Reindex_stale_queues_only_versions_indexed_with_other_chunk_settings_and_skips_open_jobs()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var b = await rig.PublishedAsync(admin, "B");          // indexed with bootstrap 2400/300
        var c = await rig.PublishedAsync(admin, "C");           // published, bootstrap settings

        var activation = await rig.ActivateAsync(admin, Input(chunk: 1000, overlap: 100));
        Assert.True(activation.ChunkSettingsChanged);
        Assert.Equal(2, activation.VersionsNeedingReindex);

        var a = await rig.RegisteredAsync(admin, "A");
        await rig.Ingestion().RunDueAsync(10, None);            // A indexed with 1000/100: up to date
        var cVersion = c.Versions![0].VersionId;
        Assert.True((await rig.Documents().ReindexAsync(admin, c.DocumentId, cVersion, None)).Succeeded);   // C (still published) has an open job
        var jobsBefore = await ctx.Db.KnowledgeIngestionJobs.CountAsync();

        var result = await rig.Config().ReindexStaleAsync(admin, None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new RagReindexStaleView(1, 2, 1, 1), result.Value);
        var queued = await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).ToListAsync();
        Assert.Equal(jobsBefore + 1, queued.Count);
        var newJob = queued.Single(j => j.VersionId == b.Versions![0].VersionId && j.Status == JobStatuses.Pending);
        Assert.Equal((1, 1000, 100), (newJob.ConfigVersion!.Value, newJob.ChunkCharacters!.Value, newJob.ChunkOverlap!.Value));
        Assert.DoesNotContain(queued, j => j.VersionId == a.Versions![0].VersionId && j.Status == JobStatuses.Pending);

        // A second call while those jobs are open queues nothing.
        Assert.Equal(0, (await rig.Config().ReindexStaleAsync(admin, None)).Value!.Queued);
        // After they run everything is up to date and B stayed published throughout.
        await rig.Ingestion().RunDueAsync(10, None);
        Assert.Equal(new RagReindexStaleView(1, 0, 0, 0), (await rig.Config().ReindexStaleAsync(admin, None)).Value);
        Assert.Contains(await new KnowledgeUnitOfWork(ctx.Db).GetRetrievableVersionsAsync(None), v => v.VersionId == b.Versions![0].VersionId);
        var validation = await rig.Config().SaveDraftAsync(admin, Input(chunk: 2000, overlap: 200), null, None);
        Assert.True(validation.Succeeded);
        var impact = (await rig.Config().ValidateDraftAsync(None)).Value!;
        Assert.True(impact.Valid);
        Assert.True(impact.ChunkSettingsChanged);
        Assert.Equal(3, impact.VersionsNeedingReindex);
    }

    [Theory]
    [InlineData("OCR_REQUIRED")]
    [InlineData("UNSUPPORTED_CONTENT")]
    [InlineData("INTEGRITY_FAILED")]
    [InlineData("FAILED")]
    public async Task A_published_version_stays_published_and_retrievable_when_its_reindex_fails(string outcome)
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        var versionId = doc.Versions![0].VersionId;
        var before = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();

        await rig.ActivateAsync(admin, Input(chunk: 1000, overlap: 100));
        rig.Rag.Ingest = (r, _) => Task.FromResult(outcome == "FAILED"
            ? new RagIngestResult(true, "FAILED", null, null, null, null, null, "embedding provider down")
            : new RagIngestResult(true, outcome, 1, 0, 0, null, null, $"{outcome} on reindex", r.ConfigVersion, r.ChunkCharacters, r.ChunkOverlap));
        Assert.Equal(1, (await rig.Config().ReindexStaleAsync(admin, None)).Value!.Queued);
        await rig.Ingestion().RunDueAsync(10, None);

        var after = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.Published, after.Status);
        Assert.Equal(before.ChunkCount, after.ChunkCount);
        Assert.Equal(before.IndexedChunkCharacters, after.IndexedChunkCharacters);
        Assert.Equal(before.IndexedConfigVersion, after.IndexedConfigVersion);
        Assert.Null(after.FailureReason);
        Assert.Equal(outcome, after.LastIngestStatus);
        Assert.False(string.IsNullOrEmpty(after.LastIngestError));
        Assert.Equal(DocumentStatuses.Published, (await ctx.Db.KnowledgeDocuments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(versionId, Assert.Single(await new KnowledgeUnitOfWork(ctx.Db).GetRetrievableVersionsAsync(None)).VersionId);
        var answer = await rig.Chat().AskAsync(admin, null, "Bảo hành bao lâu?", null, None);
        Assert.True(answer.Value!.Grounded);
    }

    [Fact]
    public async Task The_quota_estimate_follows_the_effective_configuration()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.PublishedAsync(admin);
        var reserved = new List<long>();
        rig.Rag.DuringAnswer = async () => reserved.Add((await ctx.Db.RagUsageReservations.AsNoTracking().SingleAsync()).ReservedTokens);
        const string question = "Bảo hành bao lâu?";

        await rig.Chat().AskAsync(admin, null, question, null, None);
        Assert.Equal(rig.Options.MaxContextTokens + rig.Options.MaxOutputTokens + question.Length / 3 + 500, reserved[^1]);

        await rig.ActivateAsync(admin, Input(context: 500, maxOutput: 64));
        await rig.Chat().AskAsync(admin, null, question, null, None);
        Assert.Equal(500 + 64 + question.Length / 3 + 500, reserved[^1]);

        // With a small daily allowance the active (larger) estimate is what gets refused.
        await rig.ActivateAsync(admin, Input(context: 6000, maxOutput: 1500));
        rig.Options.DailyTokensPerUser = (await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == admin)).ChargedTokens + 7000;
        Assert.Equal(KnowledgeErrorCodes.QuotaExceeded, (await rig.Chat().AskAsync(admin, null, question, null, None)).ErrorCode);
    }

    [Fact]
    public async Task Citation_grounding_is_unchanged_under_an_admin_configuration()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.ActivateAsync(admin, Input(topK: 2, minScore: 0.9m));
        var doc = await rig.PublishedAsync(admin);

        var grounded = await rig.Chat().AskAsync(admin, null, "Bảo hành bao lâu?", null, None);
        Assert.True(grounded.Value!.Grounded);
        Assert.Equal(doc.DocumentId, Assert.Single(grounded.Value.Answer.Citations).DocumentId);

        // Claimed grounded but without citations, or citing an unknown version: replaced by the insufficient answer.
        rig.Rag.Answer = _ => new RagAnswerResult(true, null, "Chắc là 25 năm.", true, [], 500, 30, "fake", 1, 1, 1);
        Assert.Equal(ChatService.InsufficientAnswer, (await rig.Chat().AskAsync(admin, null, "Inverter?", null, None)).Value!.Answer.Content);
        rig.Rag.Answer = r => new RagAnswerResult(true, null, "x [1]", true, [new RagCitation(Guid.NewGuid(), Guid.NewGuid(), "c", 1, null, 0.9, "s")], 1, 1, "fake", 1, 1, 1);
        var foreign = await rig.Chat().AskAsync(admin, null, "Inverter?", null, None);
        Assert.False(foreign.Value!.Grounded);
        Assert.Empty(foreign.Value.Answer.Citations);
    }

    /// <summary>The RAG service rejects unknown fields (extra="forbid" in app/main.py): the payload names must stay in sync.</summary>
    [Fact]
    public void Rag_payloads_use_exactly_the_field_names_the_rag_service_accepts()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        static string[] Keys(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        var ingest = new RagIngestRequest(Guid.NewGuid(), Guid.NewGuid(), "b", "k", null, "text/plain", new string('a', 64), "t", 1, 2, 1000, 100);
        Assert.Equal(new[] { "bucket", "chunkCharacters", "chunkOverlap", "configVersion", "contentType", "documentId", "expectedSha256", "objectKey", "objectVersionId",
            "title", "versionId", "versionNumber" }, Keys(System.Text.Json.JsonSerializer.Serialize(ingest, json)));
        var answer = new RagAnswerRequest("q", [new RagHistoryTurn("USER", "x")], [new RagAllowedVersion(Guid.NewGuid(), Guid.NewGuid(), "t", 1)], 3, 1000, 300, 2, "gpt-4.1",
            null, RagTokenParameters.MaxTokens, 0.2m, "v1");
        var text = SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient.AnswerPayload(answer).ToJsonString();
        Assert.Equal(new[] { "allowedVersions", "configVersion", "generationModel", "history", "maxContextTokens", "maxOutputTokens", "minScore", "promptVersion", "question",
            "temperature", "tokenParameter", "topK" }, Keys(text));
        // A null temperature is sent explicitly (the service then omits it from the provider call).
        Assert.Contains("\"temperature\":null", text);
        var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "content", "role" }, root.GetProperty("history")[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(new[] { "documentId", "title", "versionId", "versionNumber" }, root.GetProperty("allowedVersions")[0].EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("OCR_REQUIRED")]
    [InlineData("THROWS")]
    public async Task A_ready_for_review_version_is_never_demoted_by_a_failed_manual_reindex(string outcome)
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.RegisteredAsync(admin);
        await rig.Ingestion().RunDueAsync(10, None);
        var before = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.ReadyForReview, before.Status);

        var statuses = new List<string>();
        rig.Rag.Ingest = async (r, _) =>
        {
            statuses.Add((await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);   // status while the job runs
            return outcome switch
            {
                "FAILED" => new RagIngestResult(true, "FAILED", null, null, null, null, null, "embedding provider down"),
                "THROWS" => throw new InvalidOperationException("boom"),
                _ => new RagIngestResult(true, outcome, 1, 0, 0, null, null, $"{outcome} on reindex", r.ConfigVersion, r.ChunkCharacters, r.ChunkOverlap),
            };
        };
        Assert.True((await rig.Documents().ReindexAsync(admin, doc.DocumentId, before.Id, None)).Succeeded);
        Assert.Equal(VersionStatuses.ReadyForReview, (await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);   // not reset to REGISTERED
        await rig.Ingestion().RunDueAsync(10, None);

        var after = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal([VersionStatuses.ReadyForReview], statuses);                                   // never PROCESSING
        Assert.Equal(VersionStatuses.ReadyForReview, after.Status);
        Assert.Equal(before.ChunkCount, after.ChunkCount);
        Assert.Equal(before.IndexedChunkCharacters, after.IndexedChunkCharacters);
        Assert.Null(after.FailureReason);
        Assert.Equal(outcome == "OCR_REQUIRED" ? "OCR_REQUIRED" : "FAILED", after.LastIngestStatus);
        Assert.False(string.IsNullOrEmpty(after.LastIngestError));
        // Still publishable without re-ingestion.
        var current = (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!;
        Assert.True((await rig.Documents().PublishAsync(doc.DocumentId, before.Id, current.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task A_withdrawn_version_keeps_its_status_through_a_failed_manual_reindex_and_a_successful_one_refreshes_the_indexed_settings()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        var versionId = doc.Versions![0].VersionId;
        await ctx.Db.KnowledgeDocumentVersions.Where(v => v.Id == versionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatuses.Withdrawn));

        rig.Rag.Ingest = (_, _) => Task.FromResult(new RagIngestResult(true, "FAILED", null, null, null, null, null, "provider down"));
        Assert.True((await rig.Documents().ReindexAsync(admin, doc.DocumentId, versionId, None)).Succeeded);
        Assert.Equal(VersionStatuses.Withdrawn, (await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
        for (var i = 0; i < rig.Options.MaxIngestAttempts + 1; i++)
        {
            ctx.Clock.Now = ctx.Clock.Now.AddHours(2);
            await rig.Ingestion().RunDueAsync(10, None);
        }
        var failed = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.Withdrawn, failed.Status);
        Assert.Equal("FAILED", failed.LastIngestStatus);
        Assert.Null(failed.FailureReason);

        await rig.ActivateAsync(admin, Input(chunk: 1000, overlap: 100));
        rig.Rag.Ingest = null;
        Assert.True((await rig.Documents().ReindexAsync(admin, doc.DocumentId, versionId, None)).Succeeded);
        await rig.Ingestion().RunDueAsync(10, None);
        var refreshed = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.Withdrawn, refreshed.Status);
        Assert.Equal("READY", refreshed.LastIngestStatus);
        Assert.Equal((1, 1000, 100), (refreshed.IndexedConfigVersion!.Value, refreshed.IndexedChunkCharacters!.Value, refreshed.IndexedChunkOverlap!.Value));
    }

    [Fact]
    public async Task A_failed_version_still_goes_back_to_registered_on_a_manual_reindex()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        rig.Rag.IngestStatus = "OCR_REQUIRED";
        var doc = await rig.RegisteredAsync(admin);
        await rig.Ingestion().RunDueAsync(10, None);
        var version = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.OcrRequired, version.Status);

        Assert.True((await rig.Documents().ReindexAsync(admin, doc.DocumentId, version.Id, None)).Succeeded);
        Assert.Equal(VersionStatuses.Registered, (await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
        rig.Rag.IngestStatus = "READY";
        await rig.Ingestion().RunDueAsync(10, None);
        Assert.Equal(VersionStatuses.ReadyForReview, (await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void The_registry_rejects_invalid_model_entries_and_limits_only_narrow_the_hard_bounds()
    {
        Assert.Throws<InvalidOperationException>(() => new RagConfigurationRules(new RagOptions
        {
            GenerationModels = [new RagGenerationModelOptions { Id = "bad model" }],
        }));
        Assert.Throws<InvalidOperationException>(() => new RagConfigurationRules(new RagOptions
        {
            GenerationModels = [new RagGenerationModelOptions { Id = "m", TokenParameter = "max_output_tokens" }],
        }));
        Assert.Throws<InvalidOperationException>(() => new RagConfigurationRules(new RagOptions
        {
            GenerationModels = [new RagGenerationModelOptions { Id = "m" }, new RagGenerationModelOptions { Id = "m" }],
        }));
        var rules = new RagConfigurationRules(new RagOptions { ConfigurationLimits = new RagConfigurationLimits { TopKMax = 50, ChunkCharactersMin = 1000 } });
        Assert.Equal(10, rules.Limits.TopKMax);
        Assert.Equal(1000, rules.Limits.ChunkCharactersMin);
        Assert.Equal(["gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini"], rules.Models.Select(m => m.Id));
        Assert.Equal(["v1"], rules.PromptVersions);
    }
}
