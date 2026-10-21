using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Files;

namespace SmartSolar.Infrastructure.Storage;

/// <summary>
/// Read-only S3 (or S3-compatible) verification: HEAD for existence/size/version/type, a ranged GET
/// of the first bytes for magic-number detection and, when required, a streamed SHA-256 bounded by
/// the purpose's maximum size. Only the configured endpoint and allowed buckets are contacted, so a
/// client cannot make the backend fetch arbitrary URLs. Errors are reported as Unavailable (the file
/// stays pending) and never logged with credentials or object contents.
/// </summary>
public sealed class S3StorageObjectVerifier : IStorageObjectVerifier, IDisposable
{
    private const int PrefixBytes = 16;
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;
    private readonly ILogger<S3StorageObjectVerifier> _logger;

    public S3StorageObjectVerifier(StorageOptions options, ILogger<S3StorageObjectVerifier> logger)
        : this(CreateClient(options.S3), options, logger)
    {
    }

    public S3StorageObjectVerifier(IAmazonS3 s3, StorageOptions options, ILogger<S3StorageObjectVerifier> logger)
    {
        _s3 = s3;
        _options = options;
        _logger = logger;
    }

    /// <summary>Client for the configured endpoint/region and credential (shared with <see cref="S3StorageUrlSigner"/>).</summary>
    internal static IAmazonS3 CreateClient(S3VerificationOptions o)
    {
        var config = new AmazonS3Config
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 120)),
            MaxErrorRetry = 1,
            ForcePathStyle = o.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl)) config.ServiceURL = o.ServiceUrl;
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);

        return string.IsNullOrWhiteSpace(o.AccessKeyId) || string.IsNullOrWhiteSpace(o.SecretAccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey), config);
    }

    public async Task<StorageObjectProbe> ProbeAsync(StorageObjectLocation location, bool computeSha256, long maxBytes, CancellationToken cancellationToken)
    {
        if (!_options.Buckets().Contains(location.Bucket))
            return StorageObjectProbe.Unavailable("Bucket is not allowed.");

        try
        {
            // With a version id this reads exactly the verified version; reads below reuse HEAD's version.
            var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = location.Bucket, Key = location.ObjectKey, VersionId = location.VersionId }, cancellationToken);
            var size = head.ContentLength;
            if (size > maxBytes)
                return new StorageObjectProbe(StorageProbeStatus.TooLarge, size, head.Headers.ContentType, head.VersionId, head.ETag, null, null, null);

            var prefix = await ReadAsync(location, head.VersionId, 0, Math.Min(PrefixBytes, Math.Max(size, 1)) - 1, cancellationToken);
            string? sha = null;
            if (computeSha256)
            {
                using var response = await _s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = location.Bucket, Key = location.ObjectKey, VersionId = head.VersionId,
                }, cancellationToken);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        return new StorageObjectProbe(StorageProbeStatus.TooLarge, total, head.Headers.ContentType, head.VersionId, head.ETag, prefix, null, null);
                    hasher.AppendData(buffer, 0, read);
                }
                sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            return new StorageObjectProbe(StorageProbeStatus.Found, size, head.Headers.ContentType, head.VersionId, head.ETag, prefix, sha, null);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return StorageObjectProbe.Missing();
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Storage verification unavailable ({ExceptionType}).", ex.GetType().Name);
            return StorageObjectProbe.Unavailable("Storage could not be reached.");
        }
    }

    public async Task<StorageObjectProbe> HeadAsync(StorageObjectLocation location, CancellationToken cancellationToken)
    {
        if (!_options.Buckets().Contains(location.Bucket))
            return StorageObjectProbe.Unavailable("Bucket is not allowed.");
        try
        {
            var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = location.Bucket, Key = location.ObjectKey, VersionId = location.VersionId }, cancellationToken);
            return new StorageObjectProbe(StorageProbeStatus.Found, head.ContentLength, head.Headers.ContentType, head.VersionId, head.ETag, null, null, null);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return StorageObjectProbe.Missing();
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Storage HEAD unavailable ({ExceptionType}).", ex.GetType().Name);
            return StorageObjectProbe.Unavailable("Storage could not be reached.");
        }
    }

    private async Task<byte[]> ReadAsync(StorageObjectLocation location, string? versionId, long from, long to, CancellationToken cancellationToken)
    {
        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = location.Bucket, Key = location.ObjectKey, VersionId = versionId, ByteRange = new ByteRange(from, to),
        }, cancellationToken);
        using var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    public void Dispose() => _s3.Dispose();
}

/// <summary>Used when S3 verification is not configured: every file stays PENDING_VERIFICATION.</summary>
public sealed class DisabledStorageObjectVerifier : IStorageObjectVerifier
{
    public Task<StorageObjectProbe> ProbeAsync(StorageObjectLocation location, bool computeSha256, long maxBytes, CancellationToken cancellationToken)
        => Task.FromResult(StorageObjectProbe.Unavailable("Storage verification is not configured."));

    public Task<StorageObjectProbe> HeadAsync(StorageObjectLocation location, CancellationToken cancellationToken)
        => Task.FromResult(StorageObjectProbe.Unavailable("Storage verification is not configured."));
}
