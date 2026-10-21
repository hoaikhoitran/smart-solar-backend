using System.Text.Json;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>
/// Checks the inverter values in Product.Spec. Every field is optional (older catalog data may not have them),
/// but a field that is present must have the right type and a sensible value. Other keys stay free-form.
/// <code>
/// { "ratedAcPowerW": 5000, "maxDcInputPowerW": 7500, "maxDcVoltageV": 600, "mpptCount": 2, "efficiencyPercent": 97.5, "phase": "SINGLE_PHASE" }
/// </code>
/// </summary>
public static class InverterSpecRules
{
    public const string SinglePhase = "SINGLE_PHASE";
    public const string ThreePhase = "THREE_PHASE";

    private static readonly string[] PositiveNumberFields = ["ratedAcPowerW", "maxDcInputPowerW", "maxDcVoltageV"];

    /// <summary>Returns the problems found; the spec must already be a JSON object.</summary>
    public static List<string> Validate(string specJson)
    {
        var errors = new List<string>();
        using var document = JsonDocument.Parse(specJson);
        var spec = document.RootElement;

        foreach (var field in PositiveNumberFields)
        {
            if (spec.TryGetProperty(field, out var value) && !IsPositiveNumber(value))
            {
                errors.Add($"'spec.{field}' must be a number greater than 0.");
            }
        }

        if (spec.TryGetProperty("mpptCount", out var mpptCount) && !IsPositiveInteger(mpptCount))
        {
            errors.Add("'spec.mpptCount' must be a whole number greater than 0.");
        }

        if (spec.TryGetProperty("efficiencyPercent", out var efficiency) && !IsPercent(efficiency))
        {
            errors.Add("'spec.efficiencyPercent' must be a number greater than 0 and at most 100.");
        }

        if (spec.TryGetProperty("phase", out var phase) && !IsKnownPhase(phase))
        {
            errors.Add($"'spec.phase' must be {SinglePhase} or {ThreePhase}.");
        }

        return errors;
    }

    private static bool IsPositiveNumber(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0;

    private static bool IsPositiveInteger(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0;

    private static bool IsPercent(JsonElement value)
        => IsPositiveNumber(value) && value.GetDouble() <= 100;

    private static bool IsKnownPhase(JsonElement value)
        => value.ValueKind == JsonValueKind.String && value.GetString() is SinglePhase or ThreePhase;
}
