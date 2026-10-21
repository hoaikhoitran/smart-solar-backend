using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Tests.TestSupport;

/// <summary>Synthetic RAG service: no LLM, no embeddings, no network.</summary>
public sealed class FakeRagServiceClient : IRagServiceClient
{
    public bool Unavailable { get; set; }
    public string IngestStatus { get; set; } = "READY";
    public int IngestCalls;
    public int DeleteCalls;
    public int AnswerCalls;
    public RagAnswerRequest? LastAnswerRequest;
    public RagIngestRequest? LastIngestRequest;
    public readonly System.Collections.Concurrent.ConcurrentQueue<RagIngestRequest> IngestRequests = new();

    /// <summary>Runs inside AnswerAsync, e.g. to unpublish a document while "generation" is in flight.</summary>
    public Func<Task>? DuringAnswer { get; set; }

    /// <summary>By default cites the first allowed version.</summary>
    public Func<RagAnswerRequest, RagAnswerResult>? Answer { get; set; }

    /// <summary>Replaces the default ingestion behaviour, e.g. to hang until cancelled or to throw.</summary>
    public Func<RagIngestRequest, CancellationToken, Task<RagIngestResult>>? Ingest { get; set; }

    public Task<RagIngestResult> IngestAsync(RagIngestRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref IngestCalls);
        LastIngestRequest = request;
        IngestRequests.Enqueue(request);
        if (Ingest is not null) return Ingest(request, cancellationToken);
        // Like the real service: echoes the configuration version and chunk settings it used.
        return Task.FromResult(Unavailable
            ? new RagIngestResult(false, "FAILED", null, null, null, null, null, "unavailable")
            : new RagIngestResult(true, IngestStatus, 3, 12, 4000, "hashing-dev", 384, IngestStatus == "READY" ? null : IngestStatus,
                request.ConfigVersion, request.ChunkCharacters, request.ChunkOverlap));
    }

    public Task<bool> DeleteVectorsAsync(Guid versionId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref DeleteCalls);
        return Task.FromResult(!Unavailable);
    }

    public async Task<RagAnswerResult> AnswerAsync(RagAnswerRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref AnswerCalls);
        LastAnswerRequest = request;
        if (DuringAnswer is not null) await DuringAnswer();
        if (Unavailable) return new RagAnswerResult(false, "unavailable", null, false, [], 0, 0, null, 0, 0);
        if (Answer is not null) return Answer(request);
        var v = request.AllowedVersions[0];
        return new RagAnswerResult(true, null, "Tấm pin được bảo hành 12 năm [1].", true,
            [new RagCitation(v.DocumentId, v.VersionId, $"{v.VersionId:N}:0", 2, "Bảo hành", 0.82, "Bảo hành sản phẩm 12 năm")], 900, 120, "fake-llm", 10, 20);
    }
}
