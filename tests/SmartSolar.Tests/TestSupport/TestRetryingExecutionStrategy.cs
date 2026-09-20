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
    public TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1))
    {
    }

    protected override bool ShouldRetryOn(Exception exception) => false;
}
