namespace SmartSolar.Api.Contracts;

public sealed class ApiError
{
    public ApiError(string code, string message, object? details = null)
    {
        Code = code;
        Message = message;
        Details = details;
        FieldErrors = FieldErrorsOf(details);
    }

    public string Code { get; }

    public string Message { get; }

    /// <summary>Field-level validation data (field → messages); null for non-validation errors.</summary>
    public object? Details { get; }

    /// <summary>
    /// Machine-readable form of <see cref="Details"/>: one entry per message with a camelCase field path and a stable
    /// <see cref="ValidationErrorCodes">code</see>. Null when the error has no field details.
    /// </summary>
    public IReadOnlyList<FieldError>? FieldErrors { get; }

    private static IReadOnlyList<FieldError>? FieldErrorsOf(object? details) => details switch
    {
        ValidationDetails validation => validation.FieldErrors,
        // Hand-built field dictionaries (a single invalid query parameter, for example) get the generic code.
        IReadOnlyDictionary<string, string[]> fields => fields
            .SelectMany(f => f.Value.Select(m => new FieldError(ValidationErrorCodes.FieldPath(f.Key), ValidationErrorCodes.InvalidValue, m))).ToList(),
        _ => null,
    };
}
