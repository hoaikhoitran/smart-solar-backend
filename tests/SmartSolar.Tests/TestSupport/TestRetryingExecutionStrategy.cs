using Microsoft.EntityFrameworkCore.Storage;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Makes the test database behave like the production Npgsql configuration,
/// which uses EnableRetryOnFailure. EF Core forbids user-initiated transactions
/// under a retrying strategy, so this reproduces that guard on SQLite: handlers
/// must run their work through Database.CreateExecutionStrategy().
/// </summary>
public sealed class TestRetryingExecutionStrategy : ExecutionStrategy
{
    private readonly bool _retryTransient;

    public TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : this(dependencies, retryTransient: false)
    {
    }

    public TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies, bool retryTransient)
        : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
    {
        _retryTransient = retryTransient;
    }

    protected override bool ShouldRetryOn(Exception exception)
        => _retryTransient && exception is TransientTestException;
}
