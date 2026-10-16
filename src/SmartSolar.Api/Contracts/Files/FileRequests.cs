namespace SmartSolar.Api.Contracts.Files;

/// <summary>Reference to an object the frontend already uploaded. Checksum is optional and only compared.</summary>
public sealed record RegisterFileRequest(string? Purpose, string? Bucket, string? ObjectKey, string? FileName, string? ContentType, long SizeBytes, string? ChecksumSha256);
