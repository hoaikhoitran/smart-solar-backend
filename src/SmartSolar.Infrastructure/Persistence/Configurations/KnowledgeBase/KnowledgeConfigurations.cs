using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Infrastructure.Persistence.Configurations.KnowledgeBase;

public sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("knowledge_document", t =>
        {
            t.HasCheckConstraint("ck_knowledge_document_status", $"status IN ({string.Join(", ", DocumentStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_knowledge_document_published", $"(status = '{DocumentStatuses.Published}') = (published_version_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(40).IsRequired();
        builder.Property(x => x.Tags).HasColumnName("tags").HasMaxLength(1000);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.LatestVersionId).HasColumnName("latest_version_id");
        builder.Property(x => x.PublishedVersionId).HasColumnName("published_version_id");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_knowledge_document_status");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        // The latest / published version must exist AND belong to this document: composite FKs onto the
        // version's (id, document_id) alternate key. NULL means "none" (MATCH SIMPLE skips the check).
        builder.HasOne<KnowledgeDocumentVersion>().WithMany().HasForeignKey(x => new { x.LatestVersionId, x.Id })
            .HasPrincipalKey(v => new { v.Id, v.DocumentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_knowledge_document_latest_version");
        builder.HasOne<KnowledgeDocumentVersion>().WithMany().HasForeignKey(x => new { x.PublishedVersionId, x.Id })
            .HasPrincipalKey(v => new { v.Id, v.DocumentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_knowledge_document_published_version");
        builder.HasIndex(x => new { x.LatestVersionId, x.Id }).HasDatabaseName("ix_knowledge_document_latest_version");
        builder.HasIndex(x => new { x.PublishedVersionId, x.Id }).HasDatabaseName("ix_knowledge_document_published_version");
    }
}

public sealed class KnowledgeDocumentVersionConfiguration : IEntityTypeConfiguration<KnowledgeDocumentVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentVersion> builder)
    {
        builder.ToTable("knowledge_document_version", t =>
        {
            t.HasCheckConstraint("ck_knowledge_document_version_status", $"status IN ({string.Join(", ", VersionStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_knowledge_document_version_number", "version_number >= 1");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(x => x.VersionNumber).HasColumnName("version_number").IsRequired();
        builder.Property(x => x.FileId).HasColumnName("file_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.PageCount).HasColumnName("page_count");
        builder.Property(x => x.ChunkCount).HasColumnName("chunk_count");
        builder.Property(x => x.ExtractedCharacters).HasColumnName("extracted_characters");
        builder.Property(x => x.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(100);
        builder.Property(x => x.EmbeddingDimension).HasColumnName("embedding_dimension");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(300);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.IngestedAt).HasColumnName("ingested_at");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique().HasDatabaseName("ux_knowledge_document_version_number");
        builder.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("ux_knowledge_document_version_file");
        // Target of the document's latest/published composite foreign keys.
        builder.HasAlternateKey(x => new { x.Id, x.DocumentId }).HasName("ak_knowledge_document_version_id_document");
        // At most one published version per document.
        builder.HasIndex(x => new { x.DocumentId, x.Status }).IsUnique().HasFilter($"status = '{VersionStatuses.Published}'").HasDatabaseName("ux_knowledge_document_version_one_published");
        builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class KnowledgeIngestionJobConfiguration : IEntityTypeConfiguration<KnowledgeIngestionJob>
{
    public void Configure(EntityTypeBuilder<KnowledgeIngestionJob> builder)
    {
        builder.ToTable("knowledge_ingestion_job", t =>
        {
            t.HasCheckConstraint("ck_knowledge_ingestion_job_status", $"status IN ({string.Join(", ", JobStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_knowledge_ingestion_job_kind", $"kind IN ('{JobKinds.Ingest}', '{JobKinds.DeleteVectors}')");
            t.HasCheckConstraint("ck_knowledge_ingestion_job_attempts", "attempts >= 0 AND max_attempts >= 1");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(x => x.MaxAttempts).HasColumnName("max_attempts").IsRequired();
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
        builder.Property(x => x.LockedUntil).HasColumnName("locked_until");
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(300);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_knowledge_ingestion_job_idempotency");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_knowledge_ingestion_job_due");
        builder.HasOne<KnowledgeDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable("chat_session");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(100).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(x => new { x.UserId, x.UpdatedAt }).HasDatabaseName("ix_chat_session_user_updated_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_message", t =>
        {
            t.HasCheckConstraint("ck_chat_message_role", $"role IN ('{ChatRoles.User}', '{ChatRoles.Assistant}')");
            t.HasCheckConstraint("ck_chat_message_status",
                $"status IN ('{ChatMessageStatuses.Completed}', '{ChatMessageStatuses.InsufficientContext}', '{ChatMessageStatuses.Failed}')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Content).HasColumnName("content").HasMaxLength(20000).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ClientMessageId).HasColumnName("client_message_id").HasMaxLength(64);
        builder.Property(x => x.ReplyToMessageId).HasColumnName("reply_to_message_id");
        builder.Property(x => x.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(x => x.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(x => x.Model).HasColumnName("model").HasMaxLength(100);
        builder.Property(x => x.LatencyMs).HasColumnName("latency_ms");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.SessionId, x.CreatedAt }).HasDatabaseName("ix_chat_message_session_created_at");
        builder.HasIndex(x => new { x.SessionId, x.ClientMessageId }).IsUnique().HasFilter("client_message_id IS NOT NULL").HasDatabaseName("ux_chat_message_client_id");
        builder.HasIndex(x => x.ReplyToMessageId).IsUnique().HasFilter("reply_to_message_id IS NOT NULL").HasDatabaseName("ux_chat_message_one_reply");
        builder.HasOne<ChatSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.ReplyToMessageId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ChatCitationConfiguration : IEntityTypeConfiguration<ChatCitation>
{
    public void Configure(EntityTypeBuilder<ChatCitation> builder)
    {
        builder.ToTable("chat_citation");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(x => x.Rank).HasColumnName("rank").IsRequired();
        builder.Property(x => x.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(x => x.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(x => x.ChunkId).HasColumnName("chunk_id").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DocumentTitle).HasColumnName("document_title").HasMaxLength(200).IsRequired();
        builder.Property(x => x.VersionNumber).HasColumnName("version_number").IsRequired();
        builder.Property(x => x.PageNumber).HasColumnName("page_number");
        builder.Property(x => x.Section).HasColumnName("section").HasMaxLength(200);
        builder.Property(x => x.Score).HasColumnName("score").IsRequired();
        builder.Property(x => x.Snippet).HasColumnName("snippet").HasMaxLength(500);
        builder.HasIndex(x => new { x.MessageId, x.Rank }).IsUnique().HasDatabaseName("ux_chat_citation_message_rank");
        builder.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KnowledgeDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RagUsageDailyConfiguration : IEntityTypeConfiguration<RagUsageDaily>
{
    public void Configure(EntityTypeBuilder<RagUsageDaily> builder)
    {
        // user_id = 00000000-0000-0000-0000-000000000000 is the global ledger (no FK on purpose).
        builder.ToTable("rag_usage_daily", t => t.HasCheckConstraint("ck_rag_usage_daily_non_negative",
            "requests >= 0 AND input_tokens >= 0 AND output_tokens >= 0 AND estimated_tokens >= 0"));
        builder.HasKey(x => new { x.UserId, x.UsageDate });
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.UsageDate).HasColumnName("usage_date");
        builder.Property(x => x.Requests).HasColumnName("requests").IsRequired();
        builder.Property(x => x.InputTokens).HasColumnName("input_tokens").IsRequired();
        builder.Property(x => x.OutputTokens).HasColumnName("output_tokens").IsRequired();
        builder.Property(x => x.EstimatedTokens).HasColumnName("estimated_tokens").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Ignore(x => x.ChargedTokens);
    }
}

public sealed class RagUsageReservationConfiguration : IEntityTypeConfiguration<RagUsageReservation>
{
    public void Configure(EntityTypeBuilder<RagUsageReservation> builder)
    {
        builder.ToTable("rag_usage_reservation", t =>
        {
            t.HasCheckConstraint("ck_rag_usage_reservation_tokens", "reserved_tokens > 0");
            t.HasCheckConstraint("ck_rag_usage_reservation_lease", "expires_at > created_at");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.UsageDate).HasColumnName("usage_date").IsRequired();
        builder.Property(x => x.ReservedTokens).HasColumnName("reserved_tokens").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.HasIndex(x => new { x.UserId, x.UsageDate, x.ExpiresAt }).HasDatabaseName("ix_rag_usage_reservation_user_date_expires");
        builder.HasIndex(x => x.UsageDate).HasDatabaseName("ix_rag_usage_reservation_date");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

