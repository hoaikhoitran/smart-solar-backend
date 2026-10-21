using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Files;

namespace SmartSolar.Infrastructure.Storage;

/// <summary>
/// Presigned S3 PUT URLs (SigV4 query signing, computed locally: no request is sent to S3 here). Same endpoint, region, path style
/// and credential as <see cref="S3StorageUrlSigner"/>; that credential then also needs <c>s3:PutObject</c> on the upload prefixes.
/// The Content-Type is part of the signature, so the browser must send exactly that header. No ACL is set (objects stay private).
/// Only allowed buckets are signed. Neither URLs, keys nor credentials are logged.
/// </summary>
public sealed class S3StorageUploadUrlSigner : IStorageUploadUrlSigner, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<S3StorageUploadUrlSigner> _logger;

    public S3StorageUploadUrlSigner(StorageOptions options, TimeProvider time, ILogger<S3StorageUploadUrlSigner> logger)
        : this(S3StorageObjectVerifier.CreateClient(options.S3), options, time, logger)
    {
    }

    public S3StorageUploadUrlSigner(IAmazonS3 s3, StorageOptions options, TimeProvider time, ILogger<S3StorageUploadUrlSigner> logger)
    {
        _s3 = s3;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool IsConfigured => true;

    /// <summary>Plain HTTP only for an explicitly configured <c>http://</c> endpoint (MinIO/LocalStack on a developer machine).</summary>
    private bool AllowsHttp => _options.S3.ServiceUrl is { } url && url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public async Task<StorageUrlResult> CreateUploadUrlAsync(StorageObjectLocation location, string contentType, TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        if (!_options.Buckets().Contains(location.Bucket) || string.IsNullOrWhiteSpace(location.ObjectKey)
            || location.ObjectKey.StartsWith('/') || location.ObjectKey.Contains("..", StringComparison.Ordinal))
        {
            _logger.LogWarning("Upload URL refused: the object location is outside the allowed scope.");
            return StorageUrlResult.Unavailable();
        }

        var seconds = (int)Math.Ceiling(timeToLive.TotalSeconds);
        var now = _time.GetUtcNow();
        try
        {
            var url = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = location.Bucket,
                Key = location.ObjectKey,
                Verb = HttpVerb.PUT,
                ContentType = contentType,
                Protocol = AllowsHttp ? Protocol.HTTP : Protocol.HTTPS,
                Expires = now.UtcDateTime.AddSeconds(seconds),
            });
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (AllowsHttp && uri.Scheme == Uri.UriSchemeHttp)))
            {
                _logger.LogWarning("Upload URL refused: the signer produced a URL with a disallowed scheme.");
                return StorageUrlResult.Unavailable();
            }
            return new StorageUrlResult(StorageUrlStatus.Signed, url, now.AddSeconds(seconds));
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or ArgumentException
                                       or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Upload URL could not be created ({ExceptionType}).", ex.GetType().Name);
            return StorageUrlResult.Unavailable();
        }
    }

    public void Dispose() => _s3.Dispose();
}

/// <summary>Used when S3 is not configured: every upload URL request answers NotConfigured (503 STORAGE_NOT_CONFIGURED).</summary>
public sealed class DisabledStorageUploadUrlSigner : IStorageUploadUrlSigner
{
    public bool IsConfigured => false;

    public Task<StorageUrlResult> CreateUploadUrlAsync(StorageObjectLocation location, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken)
        => Task.FromResult(StorageUrlResult.NotConfigured());
}
