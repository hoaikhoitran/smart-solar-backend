namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>Handler outcome: a value, or a scheduling error code with a safe message.</summary>
public sealed record SchedulingResult<T>(T? Value, string? ErrorCode, string? Message, object? Details = null)
{
    public bool Succeeded => ErrorCode is null;

    public static SchedulingResult<T> Ok(T value) => new(value, null, null);

    public static SchedulingResult<T> Fail(string code, string message, object? details = null) => new(default, code, message, details);
}
