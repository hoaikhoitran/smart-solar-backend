namespace SmartSolar.Modules.Common.Features;

/// <summary>
/// Configuration section "Features". The Installation Flow (on-site survey → quotation → contract →
/// payment → installation) and the knowledge base (RAG chatbot) add tables that are applied by a
/// manual, approved migration. Until then both flags stay false: their endpoints answer
/// 503 FEATURE_PENDING_MIGRATION and Flow 1 never queries the new tables (for example installation
/// shifts in the scheduling day state), so deploying this code before the migration is safe.
/// </summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>Superseded key kept for backward compatibility; enables both features when the new keys are absent.</summary>
    public const string LegacyFlow2EnabledKey = "Flow2Enabled";

    public const string InstallationWorkflow = "InstallationWorkflow";
    public const string KnowledgeBase = "KnowledgeBase";

    public bool InstallationWorkflowEnabled { get; init; }

    public bool KnowledgeBaseEnabled { get; init; }

    /// <summary>True when a value came from the legacy <c>Features:Flow2Enabled</c> key (reported at startup).</summary>
    public bool UsesLegacyFlag { get; init; }

    public bool IsEnabled(string feature) => feature switch
    {
        InstallationWorkflow => InstallationWorkflowEnabled,
        KnowledgeBase => KnowledgeBaseEnabled,
        _ => false,
    };

    /// <summary>An explicit new key always wins; otherwise the legacy flag applies to both features.</summary>
    public static FeatureOptions Resolve(bool? installationWorkflowEnabled, bool? knowledgeBaseEnabled, bool? legacyFlow2Enabled) => new()
    {
        InstallationWorkflowEnabled = installationWorkflowEnabled ?? legacyFlow2Enabled ?? false,
        KnowledgeBaseEnabled = knowledgeBaseEnabled ?? legacyFlow2Enabled ?? false,
        UsesLegacyFlag = legacyFlow2Enabled is not null && (installationWorkflowEnabled is null || knowledgeBaseEnabled is null),
    };
}
