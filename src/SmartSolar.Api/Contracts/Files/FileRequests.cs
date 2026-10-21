namespace SmartSolar.Api.Contracts.Files;

/// <summary>Reference to an object the frontend already uploaded. Checksum is optional and only compared.</summary>
/// <summary>What the frontend is about to upload; the backend answers with a presigned PUT URL and the object key to use.</summary>
public sealed record PresignedUploadRequest(string? Purpose, string? FileName, string? ContentType, long SizeBytes);

public sealed record RegisterFileRequest(string? Purpose, string? Bucket, string? ObjectKey, string? FileName, string? ContentType, long SizeBytes, string? ChecksumSha256);
