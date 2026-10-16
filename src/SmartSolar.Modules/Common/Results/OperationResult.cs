namespace SmartSolar.Modules.Common.Results;

/// <summary>Use-case outcome shared by the Installation Flow and knowledge-base modules: a value, or an error code with a safe message.</summary>
public sealed record OperationResult<T>(T? Value, string? ErrorCode, string? Message, object? Details = null)
{
    public bool Succeeded => ErrorCode is null;

    public static OperationResult<T> Ok(T value) => new(value, null, null);

    public static OperationResult<T> Fail(string code, string message, object? details = null) => new(default, code, message, details);

    public OperationResult<TOther> As<TOther>() => new(default, ErrorCode, Message, Details);
}

/// <summary>Error codes shared by every module (status mapping lives in the API layer).</summary>
public static class CommonErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "RESOURCE_NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string InvalidState = "INVALID_STATE_TRANSITION";
    public const string FeaturePendingMigration = "FEATURE_PENDING_MIGRATION";
}
