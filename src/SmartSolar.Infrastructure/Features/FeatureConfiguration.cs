using Microsoft.Extensions.Configuration;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Infrastructure.Features;

/// <summary>
/// Reads <see cref="FeatureOptions"/>: <c>Features:InstallationWorkflowEnabled</c> and
/// <c>Features:KnowledgeBaseEnabled</c>, falling back to the superseded <c>Features:Flow2Enabled</c>
/// (environment variable <c>Features__Flow2Enabled</c>) so existing deployments keep their behaviour.
/// </summary>
public static class FeatureConfiguration
{
    public static FeatureOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(FeatureOptions.SectionName);
        return FeatureOptions.Resolve(
            section.GetValue<bool?>(nameof(FeatureOptions.InstallationWorkflowEnabled)),
            section.GetValue<bool?>(nameof(FeatureOptions.KnowledgeBaseEnabled)),
            section.GetValue<bool?>(FeatureOptions.LegacyFlow2EnabledKey));
    }
}
