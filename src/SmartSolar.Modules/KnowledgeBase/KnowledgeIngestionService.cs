using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>
/// Runs durable ingestion jobs: lease (conditional update that also counts the attempt) → call the RAG
/// service with no transaction open → record the outcome, fenced by the lease so a worker whose lease
/// expired cannot overwrite the worker that took over. The RAG service replaces a version's chunks
/// atomically, so a retried or duplicated job never duplicates chunks.
/// <list type="bullet">
/// <item>Provider failures, timeouts and unexpected exceptions use up an attempt and back off exponentially
/// (30 s · 2^(n-1), capped at 1 h); after MaxAttempts the job is FAILED and visible to admins.</item>
/// <item>A crashed worker leaves an expired lease: the attempt was already counted, so a job that keeps
/// crashing its workers still ends FAILED instead of looping.</item>
/// <item>Service shutdown (the caller's token) gives the lease back without using the attempt.</item>
/// <item>An INGEST job runs with the chunk settings snapshotted when it was queued (jobs queued before the snapshot columns
/// existed fall back to the effective configuration).</item>
/// <item>A version that holds live chunks (READY_FOR_REVIEW, PUBLISHED, or WITHDRAWN and re-publishable) is never demoted by a
/// reindex: it is not set to PROCESSING while the job runs, and a failed or non-READY outcome never turns it into REGISTERED/FAILED
/// (the RAG service only replaces chunks on success, so the old chunks stay usable). The outcome is recorded in
/// <see cref="KnowledgeDocumentVersion.LastIngestStatus"/> / <see cref="KnowledgeDocumentVersion.LastIngestError"/>; a READY outcome
/// also refreshes the indexed_* settings and chunk metadata. REGISTERED/FAILED versions keep the classic lifecycle.</item>
/// </list>
/// </summary>
public sealed class KnowledgeIngestionService(IKnowledgeUnitOfWork unitOfWork, IFileStore fileStore, IRagServiceClient rag, RagOptions options,
    IRagConfigurationProvider configuration, TimeProvider time, ILogger<KnowledgeIngestionService> logger)
{
    /// <summary>Without dependency injection: the effective configuration is read from the same unit of work, uncached.</summary>
    public KnowledgeIngestionService(IKnowledgeUnitOfWork unitOfWork, IFileStore fileStore, IRagServiceClient rag, RagOptions options, TimeProvider time,
        ILogger<KnowledgeIngestionService> logger)
        : this(unitOfWork, fileStore, rag, options, EffectiveRagConfigurationProvider.Uncached(unitOfWork, options, time), time, logger)
    {
    }

    /// <summary>Longer than the ingest call can take, so a live worker never loses its lease.</summary>
    private TimeSpan Lease => TimeSpan.FromSeconds(Math.Clamp(options.IngestTimeoutSeconds, 1, 1800) + 120);

    public async Task<int> RunDueAsync(int limit, CancellationToken cancellationToken)
    {
        var due = await unitOfWork.ListDueJobsAsync(time.GetUtcNow(), limit, cancellationToken);
        foreach (var id in due) await RunAsync(id, cancellationToken);
        return due.Count;
    }

    /// <summary>Returns false when another worker holds the job or it is not due.</summary>
    public async Task<bool> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var lease = now + Lease;
        if (!await unitOfWork.TryLeaseJobAsync(jobId, now, lease, cancellationToken))
        {
            if (!await unitOfWork.TryFailExhaustedJobAsync(jobId, now, cancellationToken)) return false;
            logger.LogWarning("Knowledge ingestion job {JobId} has no attempts left and is marked FAILED.", jobId);
            await RecordAsync(jobId, lease: null, ok: false, "No attempts left (the last run did not finish).", terminal: true, MarkFailed, cancellationToken);
            return true;
        }

        try
        {
            await ProcessAsync(jobId, lease, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a provider failure: hand the job back for the next worker, attempt not used.
            await unitOfWork.TryReleaseLeaseAsync(jobId, lease, time.GetUtcNow(), CancellationToken.None);
            await RestoreVersionAsync(jobId, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Knowledge ingestion job {JobId} failed unexpectedly.", jobId);
            var error = $"Unexpected error ({ex.GetType().Name}).";
            await RecordAsync(jobId, lease, ok: false, error, terminal: false, v =>
            {
                v.LastIngestStatus = "FAILED";
                v.LastIngestError = error;
                BackToRegistered(v);
            }, CancellationToken.None);
        }
        return true;
    }

    private async Task ProcessAsync(Guid jobId, DateTimeOffset lease, CancellationToken cancellationToken)
    {
        var job = (await unitOfWork.FindJobAsync(jobId, false, cancellationToken))!;
        var version = await unitOfWork.FindVersionAsync(job.VersionId, false, cancellationToken);
        if (version is null)
        {
            await RecordAsync(jobId, lease, ok: false, "version missing", terminal: true, null, cancellationToken);
            return;
        }

        if (job.Kind == JobKinds.DeleteVectors)
        {
            var deleted = await rag.DeleteVectorsAsync(version.Id, cancellationToken);
            await RecordAsync(jobId, lease, deleted, deleted ? null : "RAG service unavailable", terminal: false, null, cancellationToken);
            return;
        }

        var file = await fileStore.FindAsync(version.FileId, cancellationToken);
        if (file is null || file.Status != FileStatuses.Verified || file.VerifiedChecksumSha256 is null)
        {
            await RecordAsync(jobId, lease, ok: false, "The source file is not verified.", terminal: true,
                v =>
                {
                    v.LastIngestStatus = "FAILED";
                    v.LastIngestError = "Source file not verified.";
                    if (HoldsLiveChunks(v)) return;
                    v.Status = VersionStatuses.Failed;
                    v.FailureReason = "Source file not verified.";
                }, cancellationToken);
            return;
        }
        await SetVersionStatusAsync(version.Id, VersionStatuses.Processing, cancellationToken);
        var document = (await unitOfWork.FindDocumentAsync(version.DocumentId, false, cancellationToken))!;

        int configVersion, chunkCharacters, chunkOverlap;
        if (job is { ConfigVersion: { } cv, ChunkCharacters: { } cc, ChunkOverlap: { } co })
            (configVersion, chunkCharacters, chunkOverlap) = (cv, cc, co);
        else
        {
            var effective = await configuration.GetAsync(cancellationToken);
            (configVersion, chunkCharacters, chunkOverlap) = (effective.Version, effective.Values.ChunkCharacters, effective.Values.ChunkOverlap);
        }

        // No transaction is open here: the call can take minutes.
        var result = await rag.IngestAsync(new RagIngestRequest(version.Id, version.DocumentId, file.Bucket, file.ObjectKey, file.ObjectVersionId,
            file.VerifiedContentType!, file.VerifiedChecksumSha256, document.Title, version.VersionNumber, configVersion, chunkCharacters, chunkOverlap), cancellationToken);

        if (!result.Reached || result.Status == "FAILED")
        {
            logger.LogWarning("Ingestion of knowledge version {VersionId} failed: {Error}.", version.Id, result.Error);
            await RecordAsync(jobId, lease, ok: false, result.Error ?? "RAG service unavailable", terminal: false, v =>
            {
                v.LastIngestStatus = "FAILED";
                v.LastIngestError = Truncate(result.Error ?? "RAG service unavailable");
                if (HoldsLiveChunks(v)) return;
                BackToRegistered(v);
                v.FailureReason = Truncate(result.Error);
            }, cancellationToken);
            return;
        }

        var status = result.Status switch
        {
            "READY" => VersionStatuses.ReadyForReview,
            "OCR_REQUIRED" => VersionStatuses.OcrRequired,
            _ => VersionStatuses.UnsupportedContent,
        };
        await RecordAsync(jobId, lease, ok: true, null, terminal: true, v =>
        {
            v.LastIngestStatus = Truncate(result.Status, 30);
            v.LastIngestError = status == VersionStatuses.ReadyForReview ? null : Truncate(result.Error ?? result.Status);
            if (status != VersionStatuses.ReadyForReview && HoldsLiveChunks(v))
            {
                // The service did not replace anything: the old chunks, status and metadata stay as they were.
                logger.LogWarning("Reindex of live knowledge version {VersionId} returned {Status}; the previous chunks stay in use.", v.Id, result.Status);
                return;
            }
            // A published version being reindexed stays published (its chunks were replaced atomically).
            if (!HoldsLiveChunks(v)) v.Status = status;
            v.PageCount = result.PageCount;
            v.ChunkCount = result.ChunkCount;
            v.ExtractedCharacters = result.Characters;
            v.EmbeddingModel = result.EmbeddingModel;
            v.EmbeddingDimension = result.EmbeddingDimension;
            v.FailureReason = status == VersionStatuses.ReadyForReview ? null : Truncate(result.Error ?? status);
            v.IngestedAt = time.GetUtcNow();
            if (status == VersionStatuses.ReadyForReview)
            {
                // Only what the service reports having used counts as indexed (an older service that echoes nothing leaves the
                // version "stale", so reindex-stale picks it up again).
                v.IndexedConfigVersion = result.ConfigVersion;
                v.IndexedChunkCharacters = result.ChunkCharacters;
                v.IndexedChunkOverlap = result.ChunkOverlap;
            }
        }, cancellationToken);
    }

    /// <summary>Versions whose chunks are in use or ready to be (review, publication, re-publication without re-ingestion).</summary>
    internal static bool HoldsLiveChunks(KnowledgeDocumentVersion v) => HoldsLiveChunks(v.Status);

    internal static bool HoldsLiveChunks(string status) => status is VersionStatuses.ReadyForReview or VersionStatuses.Published or VersionStatuses.Withdrawn;

    private static void BackToRegistered(KnowledgeDocumentVersion v)
    {
        if (v.Status == VersionStatuses.Processing) v.Status = VersionStatuses.Registered;
    }

    private static void MarkFailed(KnowledgeDocumentVersion v)
    {
        if (v.LastIngestStatus != "FAILED")
        {
            v.LastIngestStatus = "FAILED";
            v.LastIngestError = "Ingestion did not complete.";
        }
        if (HoldsLiveChunks(v)) return;
        if (v.Status is VersionStatuses.Registered or VersionStatuses.Processing) v.Status = VersionStatuses.Failed;
        v.FailureReason ??= "Ingestion did not complete.";
    }

    private async Task SetVersionStatusAsync(Guid versionId, string status, CancellationToken cancellationToken)
        => await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var version = await unitOfWork.FindVersionAsync(versionId, true, token);
            // A version with live chunks keeps its status during a reindex (only the outcome is recorded afterwards).
            if (version is null || HoldsLiveChunks(version)) return (false, false);
            version.Status = status;
            version.Revision++;
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    /// <summary>After a shutdown the version is no longer being processed by anyone.</summary>
    private async Task RestoreVersionAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await unitOfWork.FindJobAsync(jobId, false, cancellationToken);
        if (job is null) return;
        await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var version = await unitOfWork.FindVersionAsync(job.VersionId, true, token);
            if (version?.Status != VersionStatuses.Processing) return (false, false);
            version.Status = VersionStatuses.Registered;
            version.Revision++;
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
    }

    /// <summary>
    /// Records the outcome of an attempt (already counted when leased). When <paramref name="lease"/> is
    /// given, nothing is written unless the job is still running under that lease (fencing).
    /// </summary>
    private async Task RecordAsync(Guid jobId, DateTimeOffset? lease, bool ok, string? error, bool terminal, Action<KnowledgeDocumentVersion>? updateVersion,
        CancellationToken cancellationToken)
        => await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var job = (await unitOfWork.FindJobAsync(jobId, true, token))!;
            if (lease is { } ours && (job.Status != JobStatuses.Running || job.LockedUntil != ours))
            {
                logger.LogWarning("Ignoring the outcome of knowledge ingestion job {JobId}: its lease expired and another worker took over.", jobId);
                return (false, false);
            }
            var now = time.GetUtcNow();
            job.LockedUntil = null;
            job.UpdatedAt = now;
            job.LastError = Truncate(error);
            if (ok)
            {
                job.Status = JobStatuses.Completed;
                job.CompletedAt = now;
            }
            else if (terminal || job.Attempts >= job.MaxAttempts)
            {
                job.Status = JobStatuses.Failed;
            }
            else
            {
                job.Status = JobStatuses.RetryWaiting;
                job.NextAttemptAt = now.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, job.Attempts - 1))));
            }

            var version = await unitOfWork.FindVersionAsync(job.VersionId, true, token);
            if (version is not null && updateVersion is not null)
            {
                updateVersion(version);
                if (job.Status == JobStatuses.Failed) MarkFailed(version);
                version.Revision++;
                var document = await unitOfWork.FindDocumentAsync(version.DocumentId, true, token);
                if (document is not null && document.Status is DocumentStatuses.Registered or DocumentStatuses.Processing or DocumentStatuses.Failed)
                {
                    document.Status = version.Status switch
                    {
                        VersionStatuses.ReadyForReview => DocumentStatuses.ReadyForReview,
                        VersionStatuses.Failed or VersionStatuses.OcrRequired or VersionStatuses.UnsupportedContent => DocumentStatuses.Failed,
                        _ => DocumentStatuses.Processing,
                    };
                    document.UpdatedAt = now;
                    document.Revision++;
                }
            }
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    private static string? Truncate(string? value, int max = 300) => value is null ? null : value.Length <= max ? value : value[..max];
}
