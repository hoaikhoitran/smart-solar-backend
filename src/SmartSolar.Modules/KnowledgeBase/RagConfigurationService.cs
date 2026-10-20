using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed record RagConfigurationView(int Version, string Status, string GenerationModel, decimal? Temperature, bool SupportsTemperature, string TokenParameter,
    int MaxOutputTokens, int ChunkCharacters, int ChunkOverlap, int TopK, decimal MinScore, int MaxContextTokens, int MaxHistoryMessages, string PromptVersion,
    string? Notes, int? BasedOnVersion, Guid? CreatedBy, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, Guid? ActivatedBy, DateTimeOffset? ActivatedAt, int Revision);

/// <param name="Bootstrap">True while no admin configuration is ACTIVE: <paramref name="Effective"/> then holds the RagOptions values (version 0).</param>
public sealed record RagConfigurationStateView(bool Bootstrap, RagConfigurationView Effective, RagConfigurationView? Active, RagConfigurationView? Draft);

public sealed record RagGenerationModelView(string Id, string DisplayName, bool SupportsTemperature, string TokenParameter, int MaxOutputTokensLimit);

public sealed record RagConfigurationLimitsView(int ChunkCharactersMin, int ChunkCharactersMax, string ChunkOverlapRule, int TopKMin, int TopKMax, decimal MinScoreMin,
    decimal MinScoreMax, int MaxContextTokensMin, int MaxContextTokensMax, int MaxOutputTokensMin, int MaxOutputTokensMax, decimal TemperatureMin, decimal TemperatureMax,
    int MaxHistoryMessagesMin, int MaxHistoryMessagesMax);

public sealed record RagModelCatalogView(IReadOnlyList<RagGenerationModelView> Models, IReadOnlyList<string> PromptVersions, RagConfigurationLimitsView Limits);

public sealed record RagConfigurationHistoryView(IReadOnlyList<RagConfigurationView> Items, int Total, int Page, int PageSize);

public sealed record RagConfigurationValidationView(int Version, bool Valid, IReadOnlyDictionary<string, string[]> Errors, bool ChunkSettingsChanged, int VersionsNeedingReindex);

public sealed record RagActivationView(RagConfigurationView Active, int? SupersededVersion, bool ChunkSettingsChanged, int VersionsNeedingReindex);

public sealed record RagReindexStaleView(int ConfigVersion, int Stale, int Queued, int SkippedOpenJob);

/// <summary>
/// Draft values from the admin. Null fields keep the current draft's value (or, when no draft exists, the active / bootstrap value).
/// <paramref name="TemperatureSpecified"/> distinguishes "keep" (false) from an explicit value or null (true).
/// </summary>
public sealed record RagConfigurationInput(string? GenerationModel, bool TemperatureSpecified, decimal? Temperature, int? MaxOutputTokens, int? ChunkCharacters,
    int? ChunkOverlap, int? TopK, decimal? MinScore, int? MaxContextTokens, int? MaxHistoryMessages, string? PromptVersion, string? Notes);

/// <summary>
/// Admin lifecycle of the chatbot runtime configuration: one DRAFT is edited (revision-checked), validated against the model
/// registry and bounds, and activated atomically (the previous ACTIVE becomes SUPERSEDED in the same transaction). A rollback
/// copies an older version into a new ACTIVE version, so history is never rewritten. Activation invalidates the local cache of
/// the effective configuration; other instances follow within the cache TTL (≤ 15 s). Changing chunk settings does not touch
/// existing chunks: <see cref="ReindexStaleAsync"/> queues a reindex of the versions indexed with other settings.
/// </summary>
public sealed class RagConfigurationService(IKnowledgeUnitOfWork unitOfWork, RagOptions options, RagConfigurationRules rules, IRagConfigurationProvider provider,
    TimeProvider time, ILogger<RagConfigurationService> logger)
{
    public async Task<RagConfigurationStateView> GetAsync(CancellationToken cancellationToken)
    {
        var active = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Active, false, cancellationToken);
        var draft = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Draft, false, cancellationToken);
        var effective = active is null ? BootstrapView() : View(active);
        return new RagConfigurationStateView(active is null, effective, active is null ? null : View(active), draft is null ? null : View(draft));
    }

    public RagModelCatalogView Catalog()
    {
        var l = rules.Limits;
        return new RagModelCatalogView(
            rules.Models.Select(m => new RagGenerationModelView(m.Id, m.DisplayName ?? m.Id, m.SupportsTemperature, m.TokenParameter, rules.MaxOutputTokensFor(m))).ToList(),
            rules.PromptVersions,
            new RagConfigurationLimitsView(l.ChunkCharactersMin, l.ChunkCharactersMax, "0 <= chunkOverlap <= chunkCharacters / 2", l.TopKMin, l.TopKMax, l.MinScoreMin,
                l.MinScoreMax, l.MaxContextTokensMin, l.MaxContextTokensMax, l.MaxOutputTokensMin, l.MaxOutputTokensMax, l.TemperatureMin, l.TemperatureMax,
                l.MaxHistoryMessagesMin, l.MaxHistoryMessagesMax));
    }

    public async Task<RagConfigurationHistoryView> HistoryAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 10_000); // bounded: (page - 1) * pageSize must not overflow into a negative OFFSET
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await unitOfWork.ListConfigurationsAsync((page - 1) * pageSize, pageSize, cancellationToken);
        return new RagConfigurationHistoryView(items.Select(View).ToList(), total, page, pageSize);
    }

    /// <summary>Creates the draft (from the active or bootstrap values) or updates it; an existing draft requires its revision.</summary>
    public async Task<OperationResult<RagConfigurationView>> SaveDraftAsync(Guid adminUserId, RagConfigurationInput input, int? expectedRevision, CancellationToken cancellationToken)
    {
        if (input.Notes is { Length: > 500 })
            return Invalid<RagConfigurationView>(new() { ["notes"] = ["At most 500 characters."] });
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<OperationResult<RagConfigurationView>>(async token =>
            {
                var now = time.GetUtcNow();
                var draft = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Draft, true, token);
                RagConfigurationValues baseline;
                if (draft is not null)
                {
                    if (expectedRevision != draft.Revision) return (Stale<RagConfigurationView>(draft.Revision), false);
                    baseline = draft.Values();
                }
                else
                {
                    if (expectedRevision is not null) return (OperationResult<RagConfigurationView>.Fail(RagConfigurationErrorCodes.DraftNotFound,
                        "There is no draft to update (it was activated or deleted). Reload and try again."), false);
                    var active = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Active, false, token);
                    baseline = active?.Values() ?? rules.Bootstrap(options).Values;
                    draft = new RagConfigurationRow(active?.Version, adminUserId, now, await unitOfWork.MaxConfigurationVersionAsync(token) + 1).Entity;
                    unitOfWork.Add(draft);
                }

                var values = Normalise(Merge(baseline, input), input);
                var errors = rules.Validate(values);
                if (errors.Count > 0) return (Invalid<RagConfigurationView>(errors), false);
                draft.Apply(values);
                if (input.Notes is not null) draft.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
                draft.UpdatedAt = now;
                draft.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<RagConfigurationView>.Ok(View(draft)), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Stale<RagConfigurationView>(null);
        }
    }

    /// <summary>Re-validates the stored draft against the current registry and bounds and reports the reindex impact.</summary>
    public async Task<OperationResult<RagConfigurationValidationView>> ValidateDraftAsync(CancellationToken cancellationToken)
    {
        var draft = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Draft, false, cancellationToken);
        if (draft is null) return OperationResult<RagConfigurationValidationView>.Fail(RagConfigurationErrorCodes.DraftNotFound, "There is no draft configuration.");
        var errors = rules.Validate(draft.Values());
        var current = (await provider.GetAsync(cancellationToken)).Values;
        var changed = !current.SameChunking(draft.ChunkCharacters, draft.ChunkOverlap);
        return OperationResult<RagConfigurationValidationView>.Ok(new RagConfigurationValidationView(draft.Version, errors.Count == 0, errors, changed,
            await CountStaleAsync(draft.Values(), cancellationToken)));
    }

    /// <summary>Activates the draft; the previous ACTIVE version becomes SUPERSEDED in the same transaction.</summary>
    public async Task<OperationResult<RagActivationView>> ActivateDraftAsync(Guid adminUserId, int expectedRevision, CancellationToken cancellationToken)
    {
        OperationResult<RagActivationView> result;
        try
        {
            result = await unitOfWork.ExecuteInTransactionAsync<OperationResult<RagActivationView>>(async token =>
            {
                var draft = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Draft, true, token);
                if (draft is null) return (OperationResult<RagActivationView>.Fail(RagConfigurationErrorCodes.DraftNotFound, "There is no draft configuration."), false);
                if (draft.Revision != expectedRevision) return (Stale<RagActivationView>(draft.Revision), false);
                var errors = rules.Validate(draft.Values());
                if (errors.Count > 0) return (Invalid<RagActivationView>(errors), false);
                var view = await ActivateAsync(draft, isNew: false, adminUserId, token);
                return (OperationResult<RagActivationView>.Ok(view), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Stale<RagActivationView>(null);
        }
        if (result.Succeeded)
        {
            provider.Invalidate();
            logger.LogInformation("RAG configuration version {Version} activated by {AdminId} (superseded {Previous}).", result.Value!.Active.Version, adminUserId,
                result.Value.SupersededVersion);
        }
        return result;
    }

    /// <summary>Creates a new version copied from <paramref name="version"/> and activates it (history is never rewritten).</summary>
    public async Task<OperationResult<RagActivationView>> RollbackAsync(Guid adminUserId, int version, CancellationToken cancellationToken)
    {
        OperationResult<RagActivationView> result;
        try
        {
            result = await unitOfWork.ExecuteInTransactionAsync<OperationResult<RagActivationView>>(async token =>
            {
                var target = await unitOfWork.FindConfigurationByVersionAsync(version, false, token);
                if (target is null) return (OperationResult<RagActivationView>.Fail(RagConfigurationErrorCodes.VersionNotFound, "Configuration version was not found."), false);
                if (target.Status != RagConfigurationStatuses.Superseded)
                    return (OperationResult<RagActivationView>.Fail(RagConfigurationErrorCodes.InvalidState,
                        target.Status == RagConfigurationStatuses.Active ? "This version is already active." : "Only a previously active version can be restored."), false);
                var errors = rules.Validate(target.Values());
                if (errors.Count > 0) return (Invalid<RagActivationView>(errors), false);
                var now = time.GetUtcNow();
                var copy = new RagConfigurationRow(target.Version, adminUserId, now, await unitOfWork.MaxConfigurationVersionAsync(token) + 1).Entity;
                copy.Apply(target.Values());
                copy.Notes = $"Rollback to version {target.Version}.";
                var view = await ActivateAsync(copy, isNew: true, adminUserId, token);
                return (OperationResult<RagActivationView>.Ok(view), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Stale<RagActivationView>(null);
        }
        if (result.Succeeded)
        {
            provider.Invalidate();
            logger.LogInformation("RAG configuration rolled back to version {Target} as version {Version} by {AdminId}.", version, result.Value!.Active.Version, adminUserId);
        }
        return result;
    }

    public async Task<OperationResult<bool>> DeleteDraftAsync(int? expectedRevision, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var draft = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Draft, true, token);
                if (draft is null) return (OperationResult<bool>.Fail(RagConfigurationErrorCodes.DraftNotFound, "There is no draft configuration."), false);
                if (expectedRevision is { } r && r != draft.Revision) return (Stale<bool>(draft.Revision), false);
                unitOfWork.Remove(draft);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Stale<bool>(null);
        }
    }

    /// <summary>
    /// Queues an INGEST job (snapshotting the active chunk settings) for every READY_FOR_REVIEW / PUBLISHED version indexed with other
    /// chunk settings. Versions with an open job are skipped. Published versions stay live: their chunks are only replaced on success.
    /// </summary>
    public async Task<OperationResult<RagReindexStaleView>> ReindexStaleAsync(Guid adminUserId, CancellationToken cancellationToken)
    {
        try
        {
            var view = await unitOfWork.ExecuteInTransactionAsync<RagReindexStaleView>(async token =>
            {
                var active = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Active, false, token);
                var config = active is null ? rules.Bootstrap(options) : rules.Effective(active);
                var stale = (await unitOfWork.GetIndexedVersionsAsync(false, token))
                    .Where(v => !config.Values.SameChunking(v.IndexedChunkCharacters, v.IndexedChunkOverlap)).ToList();
                var open = (await unitOfWork.GetVersionsWithOpenJobsAsync(token)).ToHashSet();
                var now = time.GetUtcNow();
                var queued = 0;
                foreach (var version in stale.Where(v => !open.Contains(v.Id)))
                {
                    await KnowledgeJobs.QueueAsync(unitOfWork, version.Id, JobKinds.Ingest, options.MaxIngestAttempts, config, now, token);
                    queued++;
                }
                if (queued > 0) await unitOfWork.SaveChangesAsync(token);
                return (new RagReindexStaleView(config.Version, stale.Count, queued, stale.Count - queued), queued > 0);
            }, cancellationToken);
            logger.LogInformation("Reindex of {Queued} stale knowledge versions queued by {AdminId} (config version {Version}).", view.Queued, adminUserId, view.ConfigVersion);
            return OperationResult<RagReindexStaleView>.Ok(view);
        }
        catch (PersistenceConflictException)
        {
            // A concurrent request queued the same job generation (idempotency key): nothing was written by this one.
            return OperationResult<RagReindexStaleView>.Fail(KnowledgeErrorCodes.InvalidState, "A reindex was queued concurrently. Reload and try again.");
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Supersedes the current ACTIVE row first (its own statement), then activates <paramref name="next"/> (inserted directly as ACTIVE
    /// when <paramref name="isNew"/>): the partial unique indexes never see two ACTIVE rows, and a rollback never collides with the draft.
    /// </summary>
    private async Task<RagActivationView> ActivateAsync(RagConfiguration next, bool isNew, Guid adminUserId, CancellationToken token)
    {
        var now = time.GetUtcNow();
        var previous = await unitOfWork.FindConfigurationAsync(RagConfigurationStatuses.Active, true, token);
        var before = previous is null ? rules.Bootstrap(options).Values : previous.Values();
        if (previous is not null)
        {
            previous.Status = RagConfigurationStatuses.Superseded;
            previous.UpdatedAt = now;
            previous.Revision++;
            await unitOfWork.SaveChangesAsync(token);
        }
        next.Status = RagConfigurationStatuses.Active;
        next.ActivatedByUserId = adminUserId;
        next.ActivatedAt = now;
        next.UpdatedAt = now;
        next.Revision++;
        if (isNew) unitOfWork.Add(next);
        await unitOfWork.SaveChangesAsync(token);
        var changed = !before.SameChunking(next.ChunkCharacters, next.ChunkOverlap);
        return new RagActivationView(View(next), previous?.Version, changed, await CountStaleAsync(next.Values(), token));
    }

    private async Task<int> CountStaleAsync(RagConfigurationValues values, CancellationToken token)
        => (await unitOfWork.GetIndexedVersionsAsync(false, token)).Count(v => !values.SameChunking(v.IndexedChunkCharacters, v.IndexedChunkOverlap));

    private static RagConfigurationValues Merge(RagConfigurationValues b, RagConfigurationInput i)
    {
        var model = string.IsNullOrWhiteSpace(i.GenerationModel) ? b.GenerationModel : i.GenerationModel.Trim();
        return new RagConfigurationValues(model, i.TemperatureSpecified ? i.Temperature : b.Temperature, i.MaxOutputTokens ?? b.MaxOutputTokens,
            i.ChunkCharacters ?? b.ChunkCharacters, i.ChunkOverlap ?? b.ChunkOverlap, i.TopK ?? b.TopK, i.MinScore ?? b.MinScore, i.MaxContextTokens ?? b.MaxContextTokens,
            i.MaxHistoryMessages ?? b.MaxHistoryMessages, string.IsNullOrWhiteSpace(i.PromptVersion) ? b.PromptVersion : i.PromptVersion.Trim());
    }

    /// <summary>Applies the "temperature only for models that support it" rule to a kept (not re-specified) temperature.</summary>
    private RagConfigurationValues Normalise(RagConfigurationValues v, RagConfigurationInput i)
        => !i.TemperatureSpecified && rules.FindModel(v.GenerationModel) is { SupportsTemperature: false } ? v with { Temperature = null } : v;

    private RagConfigurationView View(RagConfiguration c)
    {
        var model = rules.FindModel(c.GenerationModel);
        return new RagConfigurationView(c.Version, c.Status, c.GenerationModel, c.Temperature, model?.SupportsTemperature ?? c.Temperature is not null,
            rules.TokenParameterFor(c.GenerationModel), c.MaxOutputTokens, c.ChunkCharacters, c.ChunkOverlap, c.TopK, c.MinScore, c.MaxContextTokens, c.MaxHistoryMessages,
            c.PromptVersion, c.Notes, c.BasedOnVersion, c.CreatedByUserId, c.CreatedAt, c.UpdatedAt, c.ActivatedByUserId, c.ActivatedAt, c.Revision);
    }

    private RagConfigurationView BootstrapView()
    {
        var b = rules.Bootstrap(options);
        var v = b.Values;
        return new RagConfigurationView(0, RagConfigurationStatuses.Active, v.GenerationModel, v.Temperature, rules.FindModel(v.GenerationModel)?.SupportsTemperature ?? true,
            b.TokenParameter, v.MaxOutputTokens, v.ChunkCharacters, v.ChunkOverlap, v.TopK, v.MinScore, v.MaxContextTokens, v.MaxHistoryMessages, v.PromptVersion,
            "Bootstrap values from application settings.", null, null, null, null, null, null, 0);
    }

    private static OperationResult<T> Invalid<T>(Dictionary<string, string[]> errors)
        => OperationResult<T>.Fail(RagConfigurationErrorCodes.ValidationFailed, "One or more configuration values are invalid.", errors);

    private static OperationResult<T> Stale<T>(int? currentRevision)
        => OperationResult<T>.Fail(RagConfigurationErrorCodes.StaleRevision, "The configuration changed meanwhile. Reload and try again.",
            currentRevision is null ? null : new { currentRevision });

    /// <summary>A new, not yet activated row.</summary>
    private sealed class RagConfigurationRow(int? basedOn, Guid adminUserId, DateTimeOffset now, int version)
    {
        public RagConfiguration Entity { get; } = new()
        {
            Id = Guid.NewGuid(), Version = version, Status = RagConfigurationStatuses.Draft, BasedOnVersion = basedOn, CreatedByUserId = adminUserId, CreatedAt = now,
            UpdatedAt = now, Revision = 0,
        };
    }
}
