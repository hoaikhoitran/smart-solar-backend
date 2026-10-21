using System.Collections.Concurrent;
using SmartSolar.Modules.Files;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SYNTHETIC presigner for tests: returns a recognisable URL on a reserved test host that embeds the object key (as a real
/// presigned URL does), so tests can assert the key never leaks anywhere except the URL itself. Never calls S3.
/// </summary>
public sealed class FakeStorageUrlSigner(TimeProvider? time = null) : IStorageUrlSigner
{
    public const string Host = "https://signed.storage.test";
    public const string Signature = "SYNTHETIC-SIGNATURE-0123456789abcdef";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool IsConfigured { get; set; } = true;

    /// <summary>Forces an outcome other than Signed.</summary>
    public StorageUrlStatus? Outcome { get; set; }

    public ConcurrentQueue<(StorageObjectLocation Location, StorageUrlRequest Request)> Calls { get; } = new();

    public Task<StorageUrlResult> CreateReadUrlAsync(StorageObjectLocation location, StorageUrlRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue((location, request));
        if (!IsConfigured) return Task.FromResult(StorageUrlResult.NotConfigured());
        if (Outcome is { } forced && forced != StorageUrlStatus.Signed) return Task.FromResult(new StorageUrlResult(forced, null, null));
        var url = $"{Host}/{location.Bucket}/{location.ObjectKey}?versionId={location.VersionId}&X-Amz-Expires={(int)request.TimeToLive.TotalSeconds}"
                  + $"&disposition={request.Disposition}&X-Amz-Signature={Signature}";
        return Task.FromResult(new StorageUrlResult(StorageUrlStatus.Signed, url, _time.GetUtcNow().Add(request.TimeToLive)));
    }
}
