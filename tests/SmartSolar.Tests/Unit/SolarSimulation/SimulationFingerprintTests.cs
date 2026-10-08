using SmartSolar.Modules.SolarSimulation.CreateSimulation;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class SimulationFingerprintTests
{
    private static Dictionary<string, object?> Inputs() => new()
    {
        ["preSurveyId"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ["geometryVersion"] = 2,
        ["surfaceTilt"] = 15m,
        ["panelGapMm"] = null,
        ["obstacles"] = new[] { new { name = "Rock", xM = 2m } },
    };

    [Fact]
    public void Fingerprint_is_a_lowercase_sha256_hex_string()
    {
        var fingerprint = SimulationFingerprint.Compute(Inputs());

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Key_order_does_not_change_the_fingerprint()
    {
        var reversed = Inputs().Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(SimulationFingerprint.Compute(Inputs()), SimulationFingerprint.Compute(reversed));
    }

    [Fact]
    public void Equal_decimals_with_different_scale_hash_the_same()
    {
        var a = Inputs();
        var b = Inputs();
        b["surfaceTilt"] = 15.000m;

        Assert.Equal(SimulationFingerprint.Compute(a), SimulationFingerprint.Compute(b));
    }

    [Theory]
    [InlineData("geometryVersion", 3)]
    [InlineData("surfaceTilt", 15.5)]
    [InlineData("panelGapMm", 20)]
    public void Changing_any_input_changes_the_fingerprint(string key, double value)
    {
        var changed = Inputs();
        changed[key] = key == "geometryVersion" ? (int)value : (decimal)value;

        Assert.NotEqual(SimulationFingerprint.Compute(Inputs()), SimulationFingerprint.Compute(changed));
    }
}
