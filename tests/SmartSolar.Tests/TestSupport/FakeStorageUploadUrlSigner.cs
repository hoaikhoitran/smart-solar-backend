using System.Collections.Concurrent;
using SmartSolar.Modules.Files;

namespace SmartSolar.Tests.TestSupport;

/// <summary>SYNTHETIC presigner for uploads: returns a recognisable PUT URL on a reserved test host. Never calls S3.</summary>
public sealed class FakeStorageUploadUrlSigner(TimeProvider? time = null) : IStorageUploadUrlSigner
{
    public const string Host = "https://upload.storage.test";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool IsConfigured { get; set; } = true;

    /// <summary>Forces an outcome other than Signed.</summary>
    public StorageUrlStatus? Outcome { get; set; }

    public ConcurrentQueue<(StorageObjectLocation Location, string ContentType, TimeSpan TimeToLive)> Calls { get; } = new();

    public Task<StorageUrlResult> CreateUploadUrlAsync(StorageObjectLocation location, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        Calls.Enqueue((location, contentType, timeToLive));
        if (!IsConfigured) return Task.FromResult(StorageUrlResult.NotConfigured());
        if (Outcome is { } forced && forced != StorageUrlStatus.Signed) return Task.FromResult(new StorageUrlResult(forced, null, null));
        var url = $"{Host}/{location.Bucket}/{location.ObjectKey}?X-Amz-Expires={(int)timeToLive.TotalSeconds}&X-Amz-Signature=SYNTHETIC";
        return Task.FromResult(new StorageUrlResult(StorageUrlStatus.Signed, url, _time.GetUtcNow().Add(timeToLive)));
    }
}
