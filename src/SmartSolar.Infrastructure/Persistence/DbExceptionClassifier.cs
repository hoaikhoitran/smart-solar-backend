using Microsoft.EntityFrameworkCore;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>Provider-neutral detection of unique-constraint violations (PostgreSQL and SQLite).</summary>
internal static class DbExceptionClassifier
{
    private const string PostgresUniqueViolation = "23505";
    private const int SqliteConstraintViolation = 19;

    public static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException switch
        {
            null => false,
            { } inner when inner.GetType().Name == "PostgresException"
                => GetProperty(inner, "SqlState") as string == PostgresUniqueViolation,
            { } inner when inner.GetType().Name == "SqliteException"
                => GetProperty(inner, "SqliteErrorCode") as int? == SqliteConstraintViolation
                    && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    /// <summary>PostgreSQL SQLSTATE and constraint name of the provider exception, when available.</summary>
    public static (string? SqlState, string? ConstraintName) PostgresDetails(Exception exception)
        => exception.InnerException is { } inner && inner.GetType().Name == "PostgresException"
            ? (GetProperty(inner, "SqlState") as string, GetProperty(inner, "ConstraintName") as string)
            : (null, null);

    public static bool IsSqliteUniqueOn(Exception exception, string tableDotColumn)
        => exception.InnerException is { } inner && inner.GetType().Name == "SqliteException"
           && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
           && inner.Message.Contains(tableDotColumn, StringComparison.OrdinalIgnoreCase);

    private static object? GetProperty(Exception exception, string propertyName)
        => exception.GetType().GetProperty(propertyName)?.GetValue(exception);
}
