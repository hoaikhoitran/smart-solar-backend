using System.Globalization;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// A test model registry. Since 2026-11-17 a billed model must be enabled and priced to be usable; these entries carry SYNTHETIC prices
/// (test values, not provider prices). gpt-4.1-mini is the cheapest, so it stays the bootstrap default the configuration tests expect.
/// </summary>
public static class RagTestModels
{
    public static List<RagGenerationModelOptions> Classic() =>
    [
        Model("gpt-4.1-mini", "GPT-4.1 mini", true, RagTokenParameters.MaxTokens, 1500, 0.10m, 0.40m),
        Model("gpt-4.1", "GPT-4.1", true, RagTokenParameters.MaxTokens, 1500, 2.00m, 8.00m),
        Model("gpt-4o-mini", "GPT-4o mini", true, RagTokenParameters.MaxTokens, 1500, 0.15m, 0.60m),
        Model("o4-mini", "o4 mini", false, RagTokenParameters.MaxCompletionTokens, 1000, 1.10m, 4.40m),
    ];

    public static RagGenerationModelOptions Model(string id, string name, bool temperature, string tokenParameter, int maxOutput, decimal input, decimal output) => new()
    {
        Id = id, DisplayName = name, SupportsTemperature = temperature, TokenParameter = tokenParameter, MaxOutputTokensLimit = maxOutput,
        InputUsdPerMillion = input, CachedInputUsdPerMillion = input / 10, OutputUsdPerMillion = output, PricingSource = "synthetic test price",
    };

    /// <summary>The same registry as configuration keys (Rag:GenerationModels:i:*), for an API factory.</summary>
    public static Dictionary<string, string?> AsSettings(IReadOnlyList<RagGenerationModelOptions> models)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (m, i) in models.Select((m, i) => (m, i)))
        {
            var p = $"Rag:GenerationModels:{i}:";
            settings[p + "Id"] = m.Id;
            settings[p + "DisplayName"] = m.DisplayName;
            if (m.Provider is { } provider) settings[p + "Provider"] = provider;
            settings[p + "Enabled"] = m.Enabled ? "true" : "false";
            settings[p + "SupportsTemperature"] = m.SupportsTemperature ? "true" : "false";
            settings[p + "TokenParameter"] = m.TokenParameter;
            settings[p + "MaxOutputTokensLimit"] = m.MaxOutputTokensLimit.ToString(CultureInfo.InvariantCulture);
            if (m.InputUsdPerMillion is { } input) settings[p + "InputUsdPerMillion"] = input.ToString(CultureInfo.InvariantCulture);
            if (m.CachedInputUsdPerMillion is { } cached) settings[p + "CachedInputUsdPerMillion"] = cached.ToString(CultureInfo.InvariantCulture);
            if (m.OutputUsdPerMillion is { } output) settings[p + "OutputUsdPerMillion"] = output.ToString(CultureInfo.InvariantCulture);
            foreach (var (effort, j) in m.ReasoningEfforts.Select((e, j) => (e, j))) settings[p + $"ReasoningEfforts:{j}"] = effort;
            if (m.DefaultReasoningEffort is { } d) settings[p + "DefaultReasoningEffort"] = d;
        }
        return settings;
    }
}
