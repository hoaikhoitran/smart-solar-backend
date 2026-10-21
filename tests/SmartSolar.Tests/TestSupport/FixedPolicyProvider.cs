using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Tests.TestSupport;

/// <summary>A settings provider returning fixed values (the definitions' defaults, optionally overridden) at a fixed version.</summary>
public sealed class FixedPolicyProvider(SchedulingPolicyDefinitions definitions, IReadOnlyDictionary<string, int>? overrides = null, int version = 0)
    : ISchedulingPolicyProvider
{
    public Task<SchedulingPolicySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, int>(definitions.Defaults, StringComparer.Ordinal);
        foreach (var (key, value) in overrides ?? new Dictionary<string, int>()) values[key] = value;
        return Task.FromResult(new SchedulingPolicySnapshot(version, values));
    }

    public void Invalidate()
    {
    }
}
