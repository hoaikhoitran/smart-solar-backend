using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Files;

namespace SmartSolar.Infrastructure.Storage;

/// <summary>
/// Presigned S3 GET URLs (SigV4 query signing, a local computation: no request is sent to S3 here). Uses the same
/// endpoint, region, path style and credential as <see cref="S3StorageObjectVerifier"/>; that credential needs only
/// <c>s3:GetObject</c> and <c>s3:GetObjectVersion</c> on the served prefixes. Only allowed buckets are signed, the verified
/// version is pinned with <c>versionId</c>, and the response headers are overridden with the verified content type and
/// a sanitized Content-Disposition. URLs are HTTPS unless <c>Storage:S3:ServiceUrl</c> is explicitly <c>http://</c> (local
/// emulators). The lifetime never exceeds <c>Storage:S3:PresignedUrlTtlSeconds</c>. Neither URLs, keys nor credentials are logged.
/// </summary>
public sealed class S3StorageUrlSigner : IStorageUrlSigner, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<S3StorageUrlSigner> _logger;

    public S3StorageUrlSigner(StorageOptions options, TimeProvider time, ILogger<S3StorageUrlSigner> logger)
        : this(S3StorageObjectVerifier.CreateClient(options.S3), options, time, logger)
    {
    }

    public S3StorageUrlSigner(IAmazonS3 s3, StorageOptions options, TimeProvider time, ILogger<S3StorageUrlSigner> logger)
    {
        _s3 = s3;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool IsConfigured => true;

    /// <summary>Plain HTTP only for an explicitly configured <c>http://</c> endpoint (MinIO/LocalStack on a developer machine).</summary>
    private bool AllowsHttp => _options.S3.ServiceUrl is { } url && url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public async Task<StorageUrlResult> CreateReadUrlAsync(StorageObjectLocation location, StorageUrlRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Buckets().Contains(location.Bucket) || string.IsNullOrWhiteSpace(location.ObjectKey)
            || location.ObjectKey.StartsWith('/') || location.ObjectKey.Contains("..", StringComparison.Ordinal))
        {
            _logger.LogWarning("Presigned URL refused: the object location is outside the allowed scope.");
            return StorageUrlResult.Unavailable();
        }

        var max = TimeSpan.FromSeconds(Math.Clamp(_options.S3.PresignedUrlTtlSeconds, S3VerificationOptions.MinPresignedUrlTtlSeconds,
            S3VerificationOptions.MaxPresignedUrlTtlSeconds));
        var ttl = request.TimeToLive > TimeSpan.Zero && request.TimeToLive < max ? request.TimeToLive : max;
        var seconds = (int)Math.Ceiling(ttl.TotalSeconds);
        var now = _time.GetUtcNow();
        try
        {
            var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = location.Bucket,
                Key = location.ObjectKey,
                VersionId = string.IsNullOrEmpty(location.VersionId) ? null : location.VersionId,
                Verb = HttpVerb.GET,
                Protocol = AllowsHttp ? Protocol.HTTP : Protocol.HTTPS,
                Expires = now.UtcDateTime.AddSeconds(seconds),
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentType = request.ContentType,
                    ContentDisposition = ContentDispositionHeader.Build(request.Disposition, request.DownloadFileName),
                    CacheControl = $"private, max-age={seconds}",
                },
            });
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (AllowsHttp && uri.Scheme == Uri.UriSchemeHttp)))
            {
                _logger.LogWarning("Presigned URL refused: the signer produced a URL with a disallowed scheme.");
                return StorageUrlResult.Unavailable();
            }
            return new StorageUrlResult(StorageUrlStatus.Signed, url, now.AddSeconds(seconds));
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or ArgumentException
                                       or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Presigned URL could not be created ({ExceptionType}).", ex.GetType().Name);
            return StorageUrlResult.Unavailable();
        }
    }

    public void Dispose() => _s3.Dispose();
}

/// <summary>Used when S3 is not configured: every read URL request answers NotConfigured (503 STORAGE_NOT_CONFIGURED).</summary>
public sealed class DisabledStorageUrlSigner : IStorageUrlSigner
{
    public bool IsConfigured => false;

    public Task<StorageUrlResult> CreateReadUrlAsync(StorageObjectLocation location, StorageUrlRequest request, CancellationToken cancellationToken)
        => Task.FromResult(StorageUrlResult.NotConfigured());
}
