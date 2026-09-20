namespace SmartSolar.Api.Contracts;

/// <summary>
/// The single response envelope used by every endpoint in this feature.
/// Property names are serialized as isSuccess, traceId, data and error.
/// </summary>
public sealed class ApiResponse<T>
{
    public bool IsSuccess { get; init; }

    public string TraceId { get; init; } = string.Empty;

    public T? Data { get; init; }

    public ApiError? Error { get; init; }

    public static ApiResponse<T> Success(T data, string traceId)
        => new() { IsSuccess = true, TraceId = traceId, Data = data, Error = null };

    public static ApiResponse<T> Failure(ApiError error, string traceId)
        => new() { IsSuccess = false, TraceId = traceId, Data = default, Error = error };
}
