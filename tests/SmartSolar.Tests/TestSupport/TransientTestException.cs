namespace SmartSolar.Tests.TestSupport;

/// <summary>Marks a failure the test execution strategy should retry.</summary>
public sealed class TransientTestException : Exception
{
    public TransientTestException()
        : base("Simulated transient failure.")
    {
    }
}
