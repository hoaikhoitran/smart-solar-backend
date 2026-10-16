namespace SmartSolar.Api.Contracts.KnowledgeBase;

public sealed record RegisterKnowledgeDocumentRequest(Guid FileId, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record AddKnowledgeVersionRequest(Guid FileId);

public sealed record UpdateKnowledgeMetadataRequest(int Revision, string? Title, string? Category, IReadOnlyList<string>? Tags);

public sealed record PublishKnowledgeVersionRequest(Guid VersionId, int Revision);

public sealed record KnowledgeRevisionRequest(int Revision);

public sealed record ReindexKnowledgeVersionRequest(Guid VersionId);

public sealed record AskChatRequest(Guid? SessionId, string? Message, string? ClientMessageId);
