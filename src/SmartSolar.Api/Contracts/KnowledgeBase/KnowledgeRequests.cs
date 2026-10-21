using System.Text.Json;

namespace SmartSolar.Api.Contracts.KnowledgeBase;

public sealed record RegisterKnowledgeDocumentRequest(Guid FileId, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record AddKnowledgeVersionRequest(Guid FileId);

public sealed record UpdateKnowledgeMetadataRequest(int Revision, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record PublishKnowledgeVersionRequest(Guid VersionId, int Revision);

public sealed record KnowledgeRevisionRequest(int Revision);

public sealed record ReindexKnowledgeVersionRequest(Guid VersionId);

public sealed record AskChatRequest(Guid? SessionId, string? Message, string? ClientMessageId);

/// <summary>
/// Draft values; omitted fields keep the draft's (or, for a new draft, the active configuration's) value. <c>temperature</c>:
/// omitted = keep, <c>null</c> = none (required for models without temperature support), number = value.
/// <c>expectedRevision</c> is required when a draft already exists.
/// </summary>
public sealed record SaveRagConfigurationDraftRequest(string? GenerationModel, JsonElement Temperature, int? MaxOutputTokens, int? ChunkCharacters, int? ChunkOverlap,
    int? TopK, decimal? MinScore, int? MaxContextTokens, int? MaxHistoryMessages, string? PromptVersion, string? Notes, int? ExpectedRevision);

public sealed record ActivateRagConfigurationRequest(int ExpectedRevision);

public sealed record RollbackRagConfigurationRequest(int Version);
