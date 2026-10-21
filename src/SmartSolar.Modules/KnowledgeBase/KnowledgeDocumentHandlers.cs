using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed record KnowledgeVersionView(Guid VersionId, int VersionNumber, Guid FileId, string Status, int? PageCount, int? ChunkCount, int? ExtractedCharacters,
    string? EmbeddingModel, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? IngestedAt, DateTimeOffset? PublishedAt,
    int? IndexedConfigVersion = null, int? IndexedChunkCharacters = null, int? IndexedChunkOverlap = null, string? LastIngestStatus = null, string? LastIngestError = null);

public sealed record IngestionJobView(Guid JobId, Guid VersionId, string Kind, string Status, int Attempts, int MaxAttempts, DateTimeOffset NextAttemptAt, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, int? ConfigVersion = null, int? ChunkCharacters = null, int? ChunkOverlap = null);

/// <summary>Creates durable ingestion jobs; an INGEST job snapshots the chunk settings of the configuration it is queued under.</summary>
public static class KnowledgeJobs
{
    public static async Task<KnowledgeIngestionJob> QueueAsync(IKnowledgeUnitOfWork unitOfWork, Guid versionId, string kind, int maxAttempts, EffectiveRagConfiguration? config,
        DateTimeOffset now, CancellationToken token)
    {
        var generation = await unitOfWork.CountJobsAsync(versionId, kind, token) + 1;
        var ingest = kind == JobKinds.Ingest && config is not null;
        var job = new KnowledgeIngestionJob
        {
            Id = Guid.NewGuid(), VersionId = versionId, Kind = kind, Status = JobStatuses.Pending, IdempotencyKey = $"{versionId}:{kind}:{generation}",
            MaxAttempts = Math.Clamp(maxAttempts, 1, 20), NextAttemptAt = now, CreatedAt = now, UpdatedAt = now,
            ConfigVersion = ingest ? config!.Version : null,
            ChunkCharacters = ingest ? config!.Values.ChunkCharacters : null,
            ChunkOverlap = ingest ? config!.Values.ChunkOverlap : null,
        };
        unitOfWork.Add(job);
        return job;
    }
}

public sealed record KnowledgeDocumentView(Guid DocumentId, string Title, string Category, IReadOnlyList<string> Tags, string Status, Guid? LatestVersionId,
    Guid? PublishedVersionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, IReadOnlyList<KnowledgeVersionView>? Versions,
    IReadOnlyList<IngestionJobView>? Jobs);

/// <summary>
/// Admin lifecycle of knowledge documents: REGISTERED → PROCESSING → READY_FOR_REVIEW → PUBLISHED
/// (or FAILED / OCR_REQUIRED / UNSUPPORTED_CONTENT on the version). The frontend uploads the file to
/// S3; the backend verifies it and queues durable ingestion. Publishing a new version supersedes the
/// old one in the same transaction, so old chunks are never retrievable once the new one is live.
/// </summary>
public sealed class KnowledgeDocumentHandlers(IKnowledgeUnitOfWork unitOfWork, FileService files, RagOptions options, IRagConfigurationProvider configuration, TimeProvider time,
    ILogger<KnowledgeDocumentHandlers> logger)
{
    /// <summary>Without dependency injection: the effective configuration is read from the same unit of work, uncached.</summary>
    public KnowledgeDocumentHandlers(IKnowledgeUnitOfWork unitOfWork, FileService files, RagOptions options, TimeProvider time, ILogger<KnowledgeDocumentHandlers> logger)
        : this(unitOfWork, files, options, EffectiveRagConfigurationProvider.Uncached(unitOfWork, options, time), time, logger)
    {
    }

    public static readonly IReadOnlyList<string> Categories = ["PRODUCT", "WARRANTY", "POLICY", "INSTALLATION_GUIDE", "FAQ", "MAINTENANCE", "OTHER"];

    public async Task<OperationResult<KnowledgeDocumentView>> RegisterAsync(Guid adminUserId, Guid fileId, string title, string category, IReadOnlyList<string>? tags, CancellationToken cancellationToken)
    {
        var error = ValidateMetadata(title, category, tags);
        if (error is not null) return OperationResult<KnowledgeDocumentView>.Fail(CommonErrorCodes.ValidationFailed, error);
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.KnowledgeDocument, adminUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<KnowledgeDocumentView>();

        var now = time.GetUtcNow();
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(), Title = title.Trim(), Category = category.Trim().ToUpperInvariant(), Tags = Tags(tags), Status = DocumentStatuses.Registered,
            CreatedByUserId = adminUserId, CreatedAt = now, UpdatedAt = now,
        };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                // Two writes in one transaction: the document row must exist before its first version, and the
                // document's latest_version_id (a foreign key to that version) can only be set afterwards.
                unitOfWork.Add(document);
                await unitOfWork.SaveChangesAsync(token);
                await AddVersionAsync(document, fileId, 1, adminUserId, now, token);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<KnowledgeDocumentView>.Fail(FileErrorCodes.AlreadyLinked, "This file is already registered as a knowledge document version.");
        }
        return OperationResult<KnowledgeDocumentView>.Ok(await ViewAsync(document.Id, true, cancellationToken));
    }

    /// <summary>Uploads a new version (versioned updates; the published version stays live until the new one is published).</summary>
    public async Task<OperationResult<KnowledgeDocumentView>> AddVersionAsync(Guid adminUserId, Guid documentId, Guid fileId, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.KnowledgeDocument, adminUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<KnowledgeDocumentView>();
        return await MutateAsync(documentId, null, async (document, now, token) =>
        {
            if (document.Status == DocumentStatuses.Archived) return Fail(KnowledgeErrorCodes.InvalidState, "Archived documents cannot get new versions.");
            var versions = await unitOfWork.GetVersionsAsync(documentId, false, token);
            await AddVersionAsync(document, fileId, versions.Max(v => v.VersionNumber) + 1, adminUserId, now, token);
            if (document.Status is DocumentStatuses.Failed or DocumentStatuses.Registered) document.Status = DocumentStatuses.Registered;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    public Task<OperationResult<KnowledgeDocumentView>> UpdateMetadataAsync(Guid documentId, int revision, string title, string category, IReadOnlyList<string>? tags, CancellationToken cancellationToken)
    {
        var error = ValidateMetadata(title, category, tags);
        if (error is not null) return Task.FromResult(OperationResult<KnowledgeDocumentView>.Fail(CommonErrorCodes.ValidationFailed, error));
        return MutateAsync(documentId, revision, (document, _, _) =>
        {
            if (document.Status == DocumentStatuses.Archived) return Task.FromResult(Fail(KnowledgeErrorCodes.InvalidState, "Archived documents are read-only."));
            document.Title = title.Trim();
            document.Category = category.Trim().ToUpperInvariant();
            document.Tags = Tags(tags);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>Publishes a READY_FOR_REVIEW version; the previously published version becomes SUPERSEDED atomically.</summary>
    public Task<OperationResult<KnowledgeDocumentView>> PublishAsync(Guid documentId, Guid versionId, int revision, CancellationToken cancellationToken)
        => MutateAsync(documentId, revision, async (document, now, token) =>
        {
            if (document.Status == DocumentStatuses.Archived) return Fail(KnowledgeErrorCodes.InvalidState, "Archived documents cannot be published.");
            var versions = await unitOfWork.GetVersionsAsync(documentId, true, token);
            var version = versions.FirstOrDefault(v => v.Id == versionId);
            if (version is null) return Fail(KnowledgeErrorCodes.VersionNotFound, "Version was not found.");
            if (version.Status == VersionStatuses.Published && document.PublishedVersionId == versionId && document.Status == DocumentStatuses.Published) return OperationResult<bool>.Ok(true);
            if (version.Status is not (VersionStatuses.ReadyForReview or VersionStatuses.Withdrawn))
                return Fail(KnowledgeErrorCodes.InvalidState, "Only an ingested version that is ready for review can be published.");
            foreach (var other in versions.Where(v => v.Id != versionId && v.Status == VersionStatuses.Published))
                other.Status = VersionStatuses.Superseded;
            version.Status = VersionStatuses.Published;
            version.PublishedAt = now;
            document.PublishedVersionId = versionId;
            document.Status = DocumentStatuses.Published;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Immediately removes the document from retrieval (the chatbot only reads PublishedVersionId of PUBLISHED documents).</summary>
    public Task<OperationResult<KnowledgeDocumentView>> UnpublishAsync(Guid documentId, int revision, CancellationToken cancellationToken)
        => MutateAsync(documentId, revision, async (document, _, token) =>
        {
            if (document.Status != DocumentStatuses.Published) return Fail(KnowledgeErrorCodes.InvalidState, "The document is not published.");
            var published = (await unitOfWork.GetVersionsAsync(documentId, true, token)).FirstOrDefault(v => v.Id == document.PublishedVersionId);
            if (published is not null) published.Status = VersionStatuses.Withdrawn;
            document.PublishedVersionId = null;
            document.Status = DocumentStatuses.Unpublished;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Re-ingests a version (for example after an embedding model change). A published version stays live until re-published.</summary>
    public Task<OperationResult<KnowledgeDocumentView>> ReindexAsync(Guid adminUserId, Guid documentId, Guid versionId, CancellationToken cancellationToken)
        => MutateAsync(documentId, null, async (document, now, token) =>
        {
            if (document.Status == DocumentStatuses.Archived) return Fail(KnowledgeErrorCodes.InvalidState, "Archived documents cannot be reindexed.");
            var version = (await unitOfWork.GetVersionsAsync(documentId, true, token)).FirstOrDefault(v => v.Id == versionId);
            if (version is null) return Fail(KnowledgeErrorCodes.VersionNotFound, "Version was not found.");
            if ((await unitOfWork.GetJobsAsync(documentId, token)).Any(j => j.VersionId == versionId && j.Status is JobStatuses.Pending or JobStatuses.Running or JobStatuses.RetryWaiting))
                return Fail(KnowledgeErrorCodes.InvalidState, "An ingestion for this version is already queued.");
            await QueueAsync(versionId, JobKinds.Ingest, now, token);
            // READY_FOR_REVIEW / PUBLISHED / WITHDRAWN keep their status and chunks until a reindex succeeds.
            if (!KnowledgeIngestionService.HoldsLiveChunks(version))
            {
                version.Status = VersionStatuses.Registered;
                version.FailureReason = null;
            }
            logger.LogInformation("Reindex queued for knowledge version {VersionId} by {AdminId}.", versionId, adminUserId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Puts a FAILED ingestion job back in the queue.</summary>
    public Task<OperationResult<KnowledgeDocumentView>> RetryJobAsync(Guid documentId, Guid jobId, CancellationToken cancellationToken)
        => MutateAsync(documentId, null, async (_, now, token) =>
        {
            var job = await unitOfWork.FindJobAsync(jobId, true, token);
            if (job is null || (await unitOfWork.FindVersionAsync(job.VersionId, false, token))?.DocumentId != documentId)
                return Fail(KnowledgeErrorCodes.JobNotFound, "Job was not found.");
            if (job.Status != JobStatuses.Failed) return Fail(KnowledgeErrorCodes.InvalidState, "Only failed jobs can be retried.");
            job.Status = JobStatuses.Pending;
            job.Attempts = 0;
            job.NextAttemptAt = now;
            job.LastError = null;
            job.UpdatedAt = now;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Logical delete: unpublished at once, vectors removed by a queued job, metadata kept for audit.</summary>
    public Task<OperationResult<KnowledgeDocumentView>> ArchiveAsync(Guid documentId, int revision, CancellationToken cancellationToken)
        => MutateAsync(documentId, revision, async (document, now, token) =>
        {
            if (document.Status == DocumentStatuses.Archived) return OperationResult<bool>.Ok(true);
            foreach (var version in await unitOfWork.GetVersionsAsync(documentId, true, token))
            {
                if (version.Status is VersionStatuses.Published or VersionStatuses.ReadyForReview or VersionStatuses.Superseded or VersionStatuses.Withdrawn)
                    await QueueAsync(version.Id, JobKinds.DeleteVectors, now, token);
                if (version.Status == VersionStatuses.Published) version.Status = VersionStatuses.Withdrawn;
            }
            document.PublishedVersionId = null;
            document.Status = DocumentStatuses.Archived;
            document.ArchivedAt = now;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeDocumentView>> ListAsync(string? status, string? category, CancellationToken cancellationToken)
    {
        var list = new List<KnowledgeDocumentView>();
        foreach (var d in await unitOfWork.ListDocumentsAsync(status, category, cancellationToken)) list.Add(await ViewAsync(d.Id, false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<KnowledgeDocumentView>> GetAsync(Guid documentId, CancellationToken cancellationToken)
        => await unitOfWork.FindDocumentAsync(documentId, false, cancellationToken) is null
            ? OperationResult<KnowledgeDocumentView>.Fail(KnowledgeErrorCodes.DocumentNotFound, "Document was not found.")
            : OperationResult<KnowledgeDocumentView>.Ok(await ViewAsync(documentId, true, cancellationToken));

    // ------------------------------------------------------------------ internals

    private async Task AddVersionAsync(KnowledgeDocument document, Guid fileId, int number, Guid adminUserId, DateTimeOffset now, CancellationToken token)
    {
        var version = new KnowledgeDocumentVersion
        {
            Id = Guid.NewGuid(), DocumentId = document.Id, VersionNumber = number, FileId = fileId, Status = VersionStatuses.Registered,
            CreatedByUserId = adminUserId, CreatedAt = now,
        };
        unitOfWork.Add(version);
        document.LatestVersionId = version.Id;
        await QueueAsync(version.Id, JobKinds.Ingest, now, token);
    }

    private async Task QueueAsync(Guid versionId, string kind, DateTimeOffset now, CancellationToken token)
    {
        // INGEST jobs snapshot the effective chunk settings now; the job keeps them even if another configuration is activated later.
        var config = kind == JobKinds.Ingest ? await configuration.GetAsync(token) : null;
        await KnowledgeJobs.QueueAsync(unitOfWork, versionId, kind, options.MaxIngestAttempts, config, now, token);
    }

    private async Task<OperationResult<KnowledgeDocumentView>> MutateAsync(Guid documentId, int? revision,
        Func<KnowledgeDocument, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var document = await unitOfWork.FindDocumentAsync(documentId, true, token);
                if (document is null) return (Fail(KnowledgeErrorCodes.DocumentNotFound, "Document was not found."), false);
                if (revision is { } expected && document.Revision != expected)
                    return (OperationResult<bool>.Fail(KnowledgeErrorCodes.StaleRevision, "The document changed meanwhile. Reload and try again.", new { currentRevision = document.Revision }), false);
                var now = time.GetUtcNow();
                var result = await change(document, now, token);
                if (!result.Succeeded) return (result, false);
                document.UpdatedAt = now;
                document.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<KnowledgeDocumentView>();
        }
        catch (PersistenceConflictException ex)
        {
            return ex.Kind == PersistenceConflictException.UniqueViolation
                ? OperationResult<KnowledgeDocumentView>.Fail(FileErrorCodes.AlreadyLinked, "This file or job is already registered.")
                : OperationResult<KnowledgeDocumentView>.Fail(KnowledgeErrorCodes.StaleRevision, "The document changed meanwhile. Reload and try again.");
        }
        return OperationResult<KnowledgeDocumentView>.Ok(await ViewAsync(documentId, true, cancellationToken));
    }

    private async Task<KnowledgeDocumentView> ViewAsync(Guid documentId, bool detail, CancellationToken cancellationToken)
    {
        var d = (await unitOfWork.FindDocumentAsync(documentId, false, cancellationToken))!;
        IReadOnlyList<KnowledgeVersionView>? versions = null;
        IReadOnlyList<IngestionJobView>? jobs = null;
        if (detail)
        {
            versions = (await unitOfWork.GetVersionsAsync(documentId, false, cancellationToken)).OrderBy(v => v.VersionNumber)
                .Select(v => new KnowledgeVersionView(v.Id, v.VersionNumber, v.FileId, v.Status, v.PageCount, v.ChunkCount, v.ExtractedCharacters, v.EmbeddingModel,
                    v.FailureReason, v.CreatedAt, v.IngestedAt, v.PublishedAt, v.IndexedConfigVersion, v.IndexedChunkCharacters, v.IndexedChunkOverlap,
                    v.LastIngestStatus, v.LastIngestError)).ToList();
            jobs = (await unitOfWork.GetJobsAsync(documentId, cancellationToken)).OrderBy(j => j.CreatedAt)
                .Select(j => new IngestionJobView(j.Id, j.VersionId, j.Kind, j.Status, j.Attempts, j.MaxAttempts, j.NextAttemptAt, j.LastError, j.CreatedAt, j.CompletedAt,
                    j.ConfigVersion, j.ChunkCharacters, j.ChunkOverlap)).ToList();
        }
        return new KnowledgeDocumentView(d.Id, d.Title, d.Category, (d.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), d.Status, d.LatestVersionId,
            d.PublishedVersionId, d.CreatedAt, d.UpdatedAt, d.Revision, versions, jobs);
    }

    private static string? ValidateMetadata(string title, string category, IReadOnlyList<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200) return "A title of at most 200 characters is required.";
        if (!Categories.Contains(category?.Trim().ToUpperInvariant() ?? "")) return $"Category must be one of {string.Join(", ", Categories)}.";
        if (tags is { Count: > 20 } || tags?.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 40 || t.Contains(',')) == true) return "At most 20 tags of 1–40 characters (no commas).";
        return null;
    }

    private static string? Tags(IReadOnlyList<string>? tags)
        => tags is null || tags.Count == 0 ? null : string.Join(',', tags.Select(t => t.Trim().ToLowerInvariant()).Distinct());

    private static OperationResult<bool> Fail(string code, string message) => OperationResult<bool>.Fail(code, message);
}
