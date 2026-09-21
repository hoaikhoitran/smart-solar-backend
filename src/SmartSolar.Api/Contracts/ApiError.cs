namespace SmartSolar.Api.Contracts;

public sealed class ApiError
{
    public ApiError(string code, string message, object? details = null)
    {
        Code = code;
        Message = message;
        Details = details;
    }

    public string Code { get; }

    public string Message { get; }

    /// <summary>Field-level validation data; null for non-validation errors.</summary>
    public object? Details { get; }
}
