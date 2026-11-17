using System.Text.Json;

namespace SmartSolar.Api.Contracts.KnowledgeBase;

public sealed record RegisterKnowledgeDocumentRequest(Guid FileId, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record AddKnowledgeVersionRequest(Guid FileId);

public sealed record UpdateKnowledgeMetadataRequest(int Revision, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record PublishKnowledgeVersionRequest(Guid VersionId, int Revision);

public sealed record KnowledgeRevisionRequest(int Revision);

public sealed record ReindexKnowledgeVersionRequest(Guid VersionId);

/// <param name="Advisor">Optional structured values for configuration advice (override values recognised in the message text).</param>
public sealed record AskChatRequest(Guid? SessionId, string? Message, string? ClientMessageId, SolarAdvisorRequest? Advisor = null);

/// <summary>Configuration-advisor inputs; every field optional. <c>propertySiteId</c> must be one of the caller's own sites.</summary>
public sealed record SolarAdvisorRequest(decimal? RoofAreaM2, decimal? BudgetVnd, decimal? MonthlyConsumptionKwh, Guid? PropertySiteId, decimal? Latitude,
    decimal? Longitude, decimal? TiltDegree, decimal? AzimuthDegree, bool? WantsBattery)
{
    /// <summary>The first invalid field and its message, or null.</summary>
    public (string Field, string Message)? Problem()
    {
        if (RoofAreaM2 is { } a && (a <= 0 || a > 100_000)) return ("advisor.roofAreaM2", "Must be greater than 0 and at most 100000 m².");
        if (BudgetVnd is { } b && (b < 1_000_000 || b > 100_000_000_000)) return ("advisor.budgetVnd", "Must be between 1,000,000 and 100,000,000,000 VND.");
        if (MonthlyConsumptionKwh is { } c && (c <= 0 || c > 1_000_000)) return ("advisor.monthlyConsumptionKwh", "Must be greater than 0 and at most 1000000 kWh.");
        if (Latitude is { } la && (la < -90 || la > 90)) return ("advisor.latitude", "Must be between -90 and 90.");
        if (Longitude is { } lo && (lo < -180 || lo > 180)) return ("advisor.longitude", "Must be between -180 and 180.");
        if ((Latitude is null) != (Longitude is null)) return ("advisor.latitude", "Give both latitude and longitude, or neither.");
        if (TiltDegree is { } t && (t < 0 || t > 90)) return ("advisor.tiltDegree", "Must be between 0 and 90.");
        if (AzimuthDegree is { } z && (z < 0 || z > 360)) return ("advisor.azimuthDegree", "Must be between 0 and 360 (compass).");
        if (PropertySiteId == Guid.Empty) return ("advisor.propertySiteId", "Must be a site id.");
        return null;
    }

    public SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorInput ToInput()
        => new(RoofAreaM2, BudgetVnd, MonthlyConsumptionKwh, PropertySiteId, Latitude, Longitude, TiltDegree, AzimuthDegree, WantsBattery ?? false);
}

/// <summary>
/// Draft values; omitted fields keep the draft's (or, for a new draft, the active configuration's) value. <c>temperature</c>:
/// omitted = keep, <c>null</c> = none (required for models without temperature support), number = value.
/// <c>expectedRevision</c> is required when a draft already exists.
/// </summary>
public sealed record SaveRagConfigurationDraftRequest(string? GenerationModel, JsonElement Temperature, int? MaxOutputTokens, int? ChunkCharacters, int? ChunkOverlap,
    int? TopK, decimal? MinScore, int? MaxContextTokens, int? MaxHistoryMessages, string? PromptVersion, string? Notes, int? ExpectedRevision,
    JsonElement ReasoningEffort = default, bool? GenerationEnabled = null, int? UserRequestsPerDay = null, int? UserConcurrency = null, int? GlobalConcurrency = null,
    int? MaxQuestionCharacters = null, int? GenerationTimeoutSeconds = null, decimal? DailyBudgetUsd = null, decimal? MonthlyBudgetUsd = null,
    decimal? MonthlyIngestionBudgetUsd = null);

/// <summary>
/// Cost controls only (<c>PUT /api/admin/rag-budget</c>). Absent fields keep the active value. <c>expectedActiveVersion</c> is the version
/// returned by <c>GET /api/admin/rag-budget</c> (0 while bootstrap values apply).
/// </summary>
public sealed record UpdateRagBudgetRequest(int? ExpectedActiveVersion, decimal? DailyBudgetUsd, decimal? MonthlyBudgetUsd, decimal? MonthlyIngestionBudgetUsd,
    int? UserRequestsPerDay, int? UserConcurrency, int? GlobalConcurrency, int? MaxQuestionCharacters, int? GenerationTimeoutSeconds, bool? GenerationEnabled,
    string? Notes);

/// <summary>Admin review of an UNCERTAIN charge: the actual cost from the provider's billing (0 = not charged) and a note.</summary>
public sealed record ReviewAiUsageRequest(decimal? CostUsd, string? Note);

public sealed record ActivateRagConfigurationRequest(int ExpectedRevision);

public sealed record RollbackRagConfigurationRequest(int Version);

/// <summary>Admin reclassification of a knowledge document: audience INTERNAL or CUSTOMER, reason required.</summary>
public sealed record SetKnowledgeAudienceRequest(int Revision, string? Audience, string? Reason);
