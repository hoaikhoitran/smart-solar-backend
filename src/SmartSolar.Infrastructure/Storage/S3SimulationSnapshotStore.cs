using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Infrastructure.Storage;

/// <summary>
/// Durable simulation snapshots in S3 (or an S3-compatible store): PutObject with a SHA-256
/// checksum (S3 rejects a body that does not match), key <c>simulations/{preSurveyId}/{simulationId}.json</c>
/// in <c>SolarSimulation:Snapshots:Bucket</c>. Reads enable checksum validation and are bounded by
/// <c>MaxSnapshotBytes</c>; the caller additionally re-verifies the bytes against the SHA-256 stored in
/// PostgreSQL. Only the configured bucket is used. Errors are logged without object contents or credentials.
/// </summary>
public sealed class S3SimulationSnapshotStore : ISimulationSnapshotStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly SimulationSnapshotOptions _options;
    private readonly ILogger<S3SimulationSnapshotStore> _logger;

    public S3SimulationSnapshotStore(IAmazonS3 s3, SimulationSnapshotOptions options, ILogger<S3SimulationSnapshotStore> logger)
    {
        _s3 = s3;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public static IAmazonS3 CreateClient(SimulationSnapshotOptions o, string? accessKeyId, string? secretAccessKey)
    {
        var config = new AmazonS3Config
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 120)),
            MaxErrorRetry = 1,
            ForcePathStyle = o.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl)) config.ServiceURL = o.ServiceUrl;
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);

        return string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
    }

    public async Task<SimulationSnapshotLocation> PutAsync(Guid preSurveyId, Guid simulationId, byte[] content, CancellationToken cancellationToken)
    {
        var bucket = _options.Bucket!;
        var key = SimulationSnapshotKeys.ObjectKey(preSurveyId, simulationId);
        var sha256Hex = SnapshotIntegrity.Sha256Hex(content);
        try
        {
            using var body = new MemoryStream(content, writable: false);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = body,
                ContentType = "application/json",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256Hex)),
            }, cancellationToken);
            return new SimulationSnapshotLocation(bucket, key, sha256Hex, content.LongLength);
        }
        catch (Exception ex) when (IsStorageFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Simulation snapshot upload failed ({ExceptionType}).", ex.GetType().Name);
            throw new SimulationSnapshotUnavailableException("The snapshot store could not be written.", ex);
        }
    }

    public async Task<SnapshotReadResult> GetAsync(SimulationSnapshotLocation location, CancellationToken cancellationToken)
    {
        if (!string.Equals(location.Bucket, _options.Bucket, StringComparison.Ordinal))
        {
            return SnapshotReadResult.Unavailable();
        }

        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = location.Bucket,
                Key = location.ObjectKey,
                ChecksumMode = ChecksumMode.ENABLED,
            }, cancellationToken);
            if (response.ContentLength > _options.MaxSnapshotBytes)
            {
                return SnapshotReadResult.Unavailable();
            }

            using var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, cancellationToken);
            return new SnapshotReadResult(SnapshotReadStatus.Found, memory.ToArray());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return SnapshotReadResult.Missing();
        }
        catch (Exception ex) when (IsStorageFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Simulation snapshot read failed ({ExceptionType}).", ex.GetType().Name);
            return SnapshotReadResult.Unavailable();
        }
    }

    private static bool IsStorageFailure(Exception ex, CancellationToken cancellationToken)
        => ex is AmazonServiceException or AmazonClientException or HttpRequestException or IOException
           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    public void Dispose() => _s3.Dispose();
}

/// <summary>Used when snapshots are disabled (default): nothing is written and nothing can be read.</summary>
public sealed class DisabledSimulationSnapshotStore : ISimulationSnapshotStore
{
    public bool IsEnabled => false;

    public Task<SimulationSnapshotLocation> PutAsync(Guid preSurveyId, Guid simulationId, byte[] content, CancellationToken cancellationToken)
        => throw new SimulationSnapshotUnavailableException("Simulation snapshots are disabled.");

    public Task<SnapshotReadResult> GetAsync(SimulationSnapshotLocation location, CancellationToken cancellationToken)
        => Task.FromResult(SnapshotReadResult.Unavailable());
}
