using FluentValidation.Results;

namespace SmartSolar.Api.Contracts;

/// <summary>
/// One machine-readable validation problem: <see cref="Field"/> is the camelCase JSON path of the field (e.g. "unitPrice",
/// "images[0].url"), <see cref="Code"/> one of <see cref="ValidationErrorCodes"/>, <see cref="Message"/> the readable text.
/// </summary>
public sealed record FieldError(string Field, string Code, string Message);

/// <summary>Stable codes the frontend can translate. Every validation error carries one of them.</summary>
public static class ValidationErrorCodes
{
    public const string Required = "REQUIRED";
    public const string TooShort = "TOO_SHORT";
    public const string TooLong = "TOO_LONG";
    public const string InvalidLength = "INVALID_LENGTH";
    public const string TooSmall = "TOO_SMALL";
    public const string TooLarge = "TOO_LARGE";
    public const string OutOfRange = "OUT_OF_RANGE";
    public const string InvalidEmail = "INVALID_EMAIL";
    public const string InvalidFormat = "INVALID_FORMAT";
    public const string InvalidPrecision = "INVALID_PRECISION";
    /// <summary>Any other rule (allowed values, business validation).</summary>
    public const string InvalidValue = "INVALID_VALUE";

    /// <summary>Maps a FluentValidation error code (validator name) to a stable code.</summary>
    public static string FromValidator(string? validatorCode) => validatorCode switch
    {
        "NotEmptyValidator" or "NotNullValidator" => Required,
        "MinimumLengthValidator" => TooShort,
        "MaximumLengthValidator" => TooLong,
        "LengthValidator" or "ExactLengthValidator" => InvalidLength,
        "GreaterThanValidator" or "GreaterThanOrEqualValidator" => TooSmall,
        "LessThanValidator" or "LessThanOrEqualValidator" => TooLarge,
        "InclusiveBetweenValidator" or "ExclusiveBetweenValidator" => OutOfRange,
        "EmailValidator" => InvalidEmail,
        "RegularExpressionValidator" => InvalidFormat,
        "ScalePrecisionValidator" => InvalidPrecision,
        _ => InvalidValue,
    };

    /// <summary>"UnitPrice" → "unitPrice", "Images[0].Url" → "images[0].url", "$.status" → "status" (stable camelCase JSON path).</summary>
    public static string FieldPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "request";
        var path = name.StartsWith("$.", StringComparison.Ordinal) ? name[2..] : name;
        return string.Join('.', path.Split('.').Select(segment => segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
    }
}

/// <summary>
/// The <c>details</c> of a validation error: field name → readable messages, exactly as before (it serializes as that
/// dictionary), plus <see cref="FieldErrors"/>, which <see cref="ApiError"/> exposes as <c>error.fieldErrors</c>.
/// </summary>
public sealed class ValidationDetails : Dictionary<string, string[]>
{
    private readonly List<FieldError> _fieldErrors = [];

    public IReadOnlyList<FieldError> FieldErrors => _fieldErrors;

    public void Add(string field, string code, string message)
    {
        this[field] = TryGetValue(field, out var messages) ? [.. messages, message] : [message];
        _fieldErrors.Add(new FieldError(ValidationErrorCodes.FieldPath(field), code, message));
    }

    /// <summary>FluentValidation result → details keyed by property name (unchanged) with a stable code per failure.</summary>
    public static ValidationDetails From(ValidationResult result)
    {
        var details = new ValidationDetails();
        foreach (var failure in result.Errors)
            details.Add(failure.PropertyName, ValidationErrorCodes.FromValidator(failure.ErrorCode), failure.ErrorMessage);
        return details;
    }
}
