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
/// </list>
/// </summary>
public sealed class KnowledgeIngestionService(IKnowledgeUnitOfWork unitOfWork, IFileStore fileStore, IRagServiceClient rag, RagOptions options, TimeProvider time, ILogger<KnowledgeIngestionService> logger)
{
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
            await RecordAsync(jobId, lease, ok: false, $"Unexpected error ({ex.GetType().Name}).", terminal: false, BackToRegistered, CancellationToken.None);
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
                v => { v.Status = VersionStatuses.Failed; v.FailureReason = "Source file not verified."; }, cancellationToken);
            return;
        }
        await SetVersionStatusAsync(version.Id, VersionStatuses.Processing, cancellationToken);
        var document = (await unitOfWork.FindDocumentAsync(version.DocumentId, false, cancellationToken))!;

        // No transaction is open here: the call can take minutes.
        var result = await rag.IngestAsync(new RagIngestRequest(version.Id, version.DocumentId, file.Bucket, file.ObjectKey, file.ObjectVersionId,
            file.VerifiedContentType!, file.VerifiedChecksumSha256, document.Title, version.VersionNumber), cancellationToken);

        if (!result.Reached || result.Status == "FAILED")
        {
            logger.LogWarning("Ingestion of knowledge version {VersionId} failed: {Error}.", version.Id, result.Error);
            await RecordAsync(jobId, lease, ok: false, result.Error ?? "RAG service unavailable", terminal: false,
                v => { BackToRegistered(v); v.FailureReason = Truncate(result.Error); }, cancellationToken);
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
            // A published version being reindexed stays published (its chunks were replaced atomically).
            if (v.Status != VersionStatuses.Published || status != VersionStatuses.ReadyForReview) v.Status = status;
            v.PageCount = result.PageCount;
            v.ChunkCount = result.ChunkCount;
            v.ExtractedCharacters = result.Characters;
            v.EmbeddingModel = result.EmbeddingModel;
            v.EmbeddingDimension = result.EmbeddingDimension;
            v.FailureReason = status == VersionStatuses.ReadyForReview ? null : Truncate(result.Error ?? status);
            v.IngestedAt = time.GetUtcNow();
        }, cancellationToken);
    }

    private static void BackToRegistered(KnowledgeDocumentVersion v)
    {
        if (v.Status == VersionStatuses.Processing) v.Status = VersionStatuses.Registered;
    }

    private static void MarkFailed(KnowledgeDocumentVersion v)
    {
        if (v.Status is VersionStatuses.Registered or VersionStatuses.Processing) v.Status = VersionStatuses.Failed;
        v.FailureReason ??= "Ingestion did not complete.";
    }

    private async Task SetVersionStatusAsync(Guid versionId, string status, CancellationToken cancellationToken)
        => await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var version = await unitOfWork.FindVersionAsync(versionId, true, token);
            if (version is null || version.Status == VersionStatuses.Published) return (false, false);
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

    private static string? Truncate(string? value) => value is null ? null : value.Length <= 300 ? value : value[..300];
}
