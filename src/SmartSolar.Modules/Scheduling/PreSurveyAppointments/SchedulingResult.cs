namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>Handler outcome: a value, or a scheduling error code with a safe message.</summary>
public sealed record SchedulingResult<T>(T? Value, string? ErrorCode, string? Message, object? Details = null)
{
    public bool Succeeded => ErrorCode is null;

    public static SchedulingResult<T> Ok(T value) => new(value, null, null);

    public static SchedulingResult<T> Fail(string code, string message, object? details = null) => new(default, code, message, details);

    /// <summary>The same failure as another result type (only for failures).</summary>
    public SchedulingResult<TOther> As<TOther>() => Succeeded
        ? throw new InvalidOperationException("Only a failure can be converted.")
        : new(default, ErrorCode, Message, Details);
}
