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
            t.HasCheckConstraint("ck_knowledge_document_audience", $"audience IN ({string.Join(", ", KnowledgeAudiences.All.Select(a => $"'{a}'"))})");
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
        // Every existing document becomes INTERNAL: nothing is exposed to customers by the migration.
        builder.Property(x => x.Audience).HasColumnName("audience").HasMaxLength(20).IsRequired().HasDefaultValue(KnowledgeAudiences.Internal);
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_knowledge_document_status");
        builder.HasIndex(x => new { x.Status, x.Audience }).HasDatabaseName("ix_knowledge_document_status_audience");
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
            t.HasCheckConstraint("ck_knowledge_document_version_indexed_chunking",
                "(indexed_chunk_characters IS NULL) = (indexed_chunk_overlap IS NULL) AND (indexed_chunk_characters IS NULL OR "
                + "(indexed_chunk_characters > 0 AND indexed_chunk_overlap >= 0 AND indexed_chunk_overlap < indexed_chunk_characters))");
            t.HasCheckConstraint("ck_knowledge_document_version_indexed_config", "indexed_config_version IS NULL OR indexed_config_version >= 0");
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
        builder.Property(x => x.IndexedConfigVersion).HasColumnName("indexed_config_version");
        builder.Property(x => x.IndexedChunkCharacters).HasColumnName("indexed_chunk_characters");
        builder.Property(x => x.IndexedChunkOverlap).HasColumnName("indexed_chunk_overlap");
        builder.Property(x => x.LastIngestStatus).HasColumnName("last_ingest_status").HasMaxLength(30);
        builder.Property(x => x.LastIngestError).HasColumnName("last_ingest_error").HasMaxLength(300);
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
            t.HasCheckConstraint("ck_knowledge_ingestion_job_chunking",
                "(chunk_characters IS NULL) = (chunk_overlap IS NULL) AND (chunk_characters IS NULL OR "
                + "(chunk_characters > 0 AND chunk_overlap >= 0 AND chunk_overlap < chunk_characters))");
            t.HasCheckConstraint("ck_knowledge_ingestion_job_config_version", "config_version IS NULL OR config_version >= 0");
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
        builder.Property(x => x.ConfigVersion).HasColumnName("config_version");
        builder.Property(x => x.ChunkCharacters).HasColumnName("chunk_characters");
        builder.Property(x => x.ChunkOverlap).HasColumnName("chunk_overlap");
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
            t.HasCheckConstraint("ck_chat_message_retrieval_audience",
                $"retrieval_audience IS NULL OR retrieval_audience IN ({string.Join(", ", KnowledgeAudiences.All.Select(a => $"'{a}'"))})");
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
        builder.Property(x => x.RetrievalAudience).HasColumnName("retrieval_audience").HasMaxLength(20);
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
            t.HasCheckConstraint("ck_rag_usage_reservation_usd", "reserved_usd >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.UsageDate).HasColumnName("usage_date").IsRequired();
        builder.Property(x => x.ReservedTokens).HasColumnName("reserved_tokens").IsRequired();
        // Defaults keep the previous API build working (it inserts neither column) during a rolling deployment.
        builder.Property(x => x.ReservedUsd).HasColumnName("reserved_usd").HasPrecision(14, 8).IsRequired().HasDefaultValue(0m);
        builder.Property(x => x.ClientMessageId).HasColumnName("client_message_id").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.HasIndex(x => new { x.UserId, x.UsageDate, x.ExpiresAt }).HasDatabaseName("ix_rag_usage_reservation_user_date_expires");
        // A question being answered cannot be admitted a second time (double click, retry, two replicas).
        builder.HasIndex(x => new { x.UserId, x.ClientMessageId }, "ux_rag_usage_reservation_client_message").IsUnique()
            .HasFilter("client_message_id IS NOT NULL");
        builder.HasIndex(x => x.UsageDate).HasDatabaseName("ix_rag_usage_reservation_date");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}


/// <summary>
/// Admin runtime configuration of the chatbot. Hard bounds are check constraints (configured limits can only narrow them);
/// partial unique indexes allow at most one ACTIVE and one DRAFT row. No secrets and no URLs are stored here.
/// </summary>
public sealed class RagConfigurationConfiguration : IEntityTypeConfiguration<RagConfiguration>
{
    public void Configure(EntityTypeBuilder<RagConfiguration> builder)
    {
        var h = RagConfigurationLimits.Hard;
        static string D(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder.ToTable("rag_configuration", t =>
        {
            t.HasCheckConstraint("ck_rag_configuration_status", $"status IN ({string.Join(", ", RagConfigurationStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_rag_configuration_version", "version >= 1 AND (based_on_version IS NULL OR based_on_version >= 1)");
            t.HasCheckConstraint("ck_rag_configuration_activated",
                $"(status = '{RagConfigurationStatuses.Draft}') = (activated_at IS NULL) AND (activated_at IS NULL) = (activated_by_user_id IS NULL)");
            t.HasCheckConstraint("ck_rag_configuration_model", "length(generation_model) > 0 AND length(prompt_version) > 0");
            t.HasCheckConstraint("ck_rag_configuration_temperature",
                $"temperature IS NULL OR (temperature >= {D(h.TemperatureMin)} AND temperature <= {D(h.TemperatureMax)})");
            t.HasCheckConstraint("ck_rag_configuration_max_output_tokens", $"max_output_tokens BETWEEN {h.MaxOutputTokensMin} AND {h.MaxOutputTokensMax}");
            t.HasCheckConstraint("ck_rag_configuration_chunking",
                $"chunk_characters BETWEEN {h.ChunkCharactersMin} AND {h.ChunkCharactersMax} AND chunk_overlap >= 0 AND chunk_overlap * 2 <= chunk_characters "
                + "AND chunk_overlap < chunk_characters");
            t.HasCheckConstraint("ck_rag_configuration_top_k", $"top_k BETWEEN {h.TopKMin} AND {h.TopKMax}");
            t.HasCheckConstraint("ck_rag_configuration_min_score", $"min_score >= {D(h.MinScoreMin)} AND min_score <= {D(h.MinScoreMax)}");
            t.HasCheckConstraint("ck_rag_configuration_max_context_tokens", $"max_context_tokens BETWEEN {h.MaxContextTokensMin} AND {h.MaxContextTokensMax}");
            t.HasCheckConstraint("ck_rag_configuration_max_history_messages",
                $"max_history_messages BETWEEN {h.MaxHistoryMessagesMin} AND {h.MaxHistoryMessagesMax}");
            // Cost controls: the hard ceilings (configured limits and the admin UI can only stay below them).
            t.HasCheckConstraint("ck_rag_configuration_reasoning_effort",
                $"reasoning_effort IS NULL OR reasoning_effort IN ({string.Join(", ", RagReasoningEfforts.All.Select(e => $"'{e}'"))})");
            t.HasCheckConstraint("ck_rag_configuration_quotas",
                $"user_requests_per_day BETWEEN {h.UserRequestsPerDayMin} AND {h.UserRequestsPerDayMax} "
                + $"AND user_concurrency BETWEEN {h.UserConcurrencyMin} AND {h.UserConcurrencyMax} "
                + $"AND global_concurrency BETWEEN {h.GlobalConcurrencyMin} AND {h.GlobalConcurrencyMax} "
                + $"AND max_question_characters BETWEEN {h.MaxQuestionCharactersMin} AND {h.MaxQuestionCharactersMax} "
                + $"AND generation_timeout_seconds BETWEEN {h.GenerationTimeoutSecondsMin} AND {h.GenerationTimeoutSecondsMax}");
            t.HasCheckConstraint("ck_rag_configuration_budgets",
                $"daily_budget_usd >= 0 AND daily_budget_usd <= {D(h.DailyBudgetUsdMax)} AND monthly_budget_usd >= 0 AND monthly_budget_usd <= {D(h.MonthlyBudgetUsdMax)} "
                + $"AND monthly_ingestion_budget_usd >= 0 AND monthly_ingestion_budget_usd <= {D(h.MonthlyIngestionBudgetUsdMax)} AND daily_budget_usd <= monthly_budget_usd");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.GenerationModel).HasColumnName("generation_model").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Temperature).HasColumnName("temperature").HasPrecision(3, 2);
        builder.Property(x => x.MaxOutputTokens).HasColumnName("max_output_tokens").IsRequired();
        builder.Property(x => x.ChunkCharacters).HasColumnName("chunk_characters").IsRequired();
        builder.Property(x => x.ChunkOverlap).HasColumnName("chunk_overlap").IsRequired();
        builder.Property(x => x.TopK).HasColumnName("top_k").IsRequired();
        builder.Property(x => x.MinScore).HasColumnName("min_score").HasPrecision(4, 3).IsRequired();
        builder.Property(x => x.MaxContextTokens).HasColumnName("max_context_tokens").IsRequired();
        builder.Property(x => x.MaxHistoryMessages).HasColumnName("max_history_messages").IsRequired();
        builder.Property(x => x.PromptVersion).HasColumnName("prompt_version").HasMaxLength(20).IsRequired();
        // Defaults = the initial policy, so existing rows and the previous API build (which inserts none of these) stay valid. The sentinel is
        // the default itself: EF otherwise treats the CLR default (false / 0) as "not set" and the database default would silently replace an
        // explicit false (generation switched off) or a 0 USD budget.
        builder.Property(x => x.ReasoningEffort).HasColumnName("reasoning_effort").HasMaxLength(10);
        builder.Property(x => x.GenerationEnabled).HasColumnName("generation_enabled").IsRequired().HasDefaultValue(true).HasSentinel(true);
        builder.Property(x => x.UserRequestsPerDay).HasColumnName("user_requests_per_day").IsRequired()
            .HasDefaultValue(RagCostDefaults.UserRequestsPerDay).HasSentinel(RagCostDefaults.UserRequestsPerDay);
        builder.Property(x => x.UserConcurrency).HasColumnName("user_concurrency").IsRequired()
            .HasDefaultValue(RagCostDefaults.UserConcurrency).HasSentinel(RagCostDefaults.UserConcurrency);
        builder.Property(x => x.GlobalConcurrency).HasColumnName("global_concurrency").IsRequired()
            .HasDefaultValue(RagCostDefaults.GlobalConcurrency).HasSentinel(RagCostDefaults.GlobalConcurrency);
        builder.Property(x => x.MaxQuestionCharacters).HasColumnName("max_question_characters").IsRequired()
            .HasDefaultValue(RagCostDefaults.MaxQuestionCharacters).HasSentinel(RagCostDefaults.MaxQuestionCharacters);
        builder.Property(x => x.GenerationTimeoutSeconds).HasColumnName("generation_timeout_seconds").IsRequired()
            .HasDefaultValue(RagCostDefaults.GenerationTimeoutSeconds).HasSentinel(RagCostDefaults.GenerationTimeoutSeconds);
        builder.Property(x => x.DailyBudgetUsd).HasColumnName("daily_budget_usd").HasPrecision(10, 2).IsRequired()
            .HasDefaultValue(RagCostDefaults.DailyBudgetUsd).HasSentinel(RagCostDefaults.DailyBudgetUsd);
        builder.Property(x => x.MonthlyBudgetUsd).HasColumnName("monthly_budget_usd").HasPrecision(10, 2).IsRequired()
            .HasDefaultValue(RagCostDefaults.MonthlyBudgetUsd).HasSentinel(RagCostDefaults.MonthlyBudgetUsd);
        builder.Property(x => x.MonthlyIngestionBudgetUsd).HasColumnName("monthly_ingestion_budget_usd").HasPrecision(10, 2).IsRequired()
            .HasDefaultValue(RagCostDefaults.MonthlyIngestionBudgetUsd).HasSentinel(RagCostDefaults.MonthlyIngestionBudgetUsd);
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
        builder.Property(x => x.BasedOnVersion).HasColumnName("based_on_version");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.ActivatedByUserId).HasColumnName("activated_by_user_id");
        builder.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Version).IsUnique().HasDatabaseName("ux_rag_configuration_version");
        builder.HasIndex(x => x.Status, "ux_rag_configuration_one_active").IsUnique().HasFilter($"status = '{RagConfigurationStatuses.Active}'");
        builder.HasIndex(x => x.Status, "ux_rag_configuration_one_draft").IsUnique().HasFilter($"status = '{RagConfigurationStatuses.Draft}'");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_rag_configuration_user_account_created_by_user_id");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActivatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_rag_configuration_user_account_activated_by_user_id");
        builder.HasIndex(x => x.CreatedByUserId).HasDatabaseName("ix_rag_configuration_created_by_user_id");
        builder.HasIndex(x => x.ActivatedByUserId).HasDatabaseName("ix_rag_configuration_activated_by_user_id");
    }
}

public sealed class KnowledgeDocumentAudienceChangeConfiguration : IEntityTypeConfiguration<KnowledgeDocumentAudienceChange>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocumentAudienceChange> builder)
    {
        builder.ToTable("knowledge_document_audience_change", t =>
        {
            var audiences = string.Join(", ", KnowledgeAudiences.All.Select(a => $"'{a}'"));
            t.HasCheckConstraint("ck_knowledge_document_audience_change_values", $"from_audience IN ({audiences}) AND to_audience IN ({audiences}) AND from_audience <> to_audience");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(x => x.FromAudience).HasColumnName("from_audience").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ToAudience).HasColumnName("to_audience").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.DocumentId, x.CreatedAt }).HasDatabaseName("ix_knowledge_document_audience_change_document_created_at");
        builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}


/// <summary>Append-mostly AI cost ledger (one row per chat question or ingestion attempt). No prompt, answer or key is stored.</summary>
public sealed class AiUsageLedgerConfiguration : IEntityTypeConfiguration<AiUsageLedgerEntry>
{
    public void Configure(EntityTypeBuilder<AiUsageLedgerEntry> builder)
    {
        static string In(IEnumerable<string> values) => "(" + string.Join(", ", values.Select(v => $"'{v}'")) + ")";
        builder.ToTable("ai_usage_ledger", t =>
        {
            t.HasCheckConstraint("ck_ai_usage_ledger_operation", $"operation IN {In(AiUsageOperations.All)}");
            t.HasCheckConstraint("ck_ai_usage_ledger_status", $"status IN {In(AiUsageStatuses.All)}");
            t.HasCheckConstraint("ck_ai_usage_ledger_outcome", $"outcome IN {In(AiUsageOutcomes.All)}");
            t.HasCheckConstraint("ck_ai_usage_ledger_non_negative",
                "embedding_tokens >= 0 AND input_tokens >= 0 AND cached_input_tokens >= 0 AND output_tokens >= 0 AND reasoning_tokens >= 0 "
                + "AND embedding_cost_usd >= 0 AND generation_cost_usd >= 0 AND reserved_usd >= 0 AND cost_usd >= 0 AND cached_input_tokens <= input_tokens");
            t.HasCheckConstraint("ck_ai_usage_ledger_settled",
                $"(status = '{AiUsageStatuses.Reserved}') = (settled_at IS NULL)");
            t.HasCheckConstraint("ck_ai_usage_ledger_lease", "expires_at > created_at");
            t.HasCheckConstraint("ck_ai_usage_ledger_review", "(reviewed_at IS NULL) = (reviewed_by_user_id IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(120).IsRequired();
        builder.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(30).IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.UserRole).HasColumnName("user_role").HasMaxLength(30);
        builder.Property(x => x.ChatMessageId).HasColumnName("chat_message_id");
        builder.Property(x => x.IngestionJobId).HasColumnName("ingestion_job_id");
        builder.Property(x => x.VersionId).HasColumnName("version_id");
        builder.Property(x => x.ConfigVersion).HasColumnName("config_version");
        builder.Property(x => x.EmbeddingProvider).HasColumnName("embedding_provider").HasMaxLength(30);
        builder.Property(x => x.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(100);
        builder.Property(x => x.EmbeddingTokens).HasColumnName("embedding_tokens").IsRequired();
        builder.Property(x => x.EmbeddingCostUsd).HasColumnName("embedding_cost_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.GenerationProvider).HasColumnName("generation_provider").HasMaxLength(30);
        builder.Property(x => x.GenerationModel).HasColumnName("generation_model").HasMaxLength(100);
        builder.Property(x => x.InputTokens).HasColumnName("input_tokens").IsRequired();
        builder.Property(x => x.CachedInputTokens).HasColumnName("cached_input_tokens").IsRequired();
        builder.Property(x => x.OutputTokens).HasColumnName("output_tokens").IsRequired();
        builder.Property(x => x.ReasoningTokens).HasColumnName("reasoning_tokens").IsRequired();
        builder.Property(x => x.GenerationCostUsd).HasColumnName("generation_cost_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.UsageReported).HasColumnName("usage_reported").IsRequired();
        builder.Property(x => x.ReservedUsd).HasColumnName("reserved_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.CostUsd).HasColumnName("cost_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.LatencyMs).HasColumnName("latency_ms");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.SettledAt).HasColumnName("settled_at");
        builder.Property(x => x.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        builder.Property(x => x.ReviewNote).HasColumnName("review_note").HasMaxLength(500);
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_ai_usage_ledger_idempotency_key");
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_ai_usage_ledger_created_at");
        builder.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_ai_usage_ledger_status_expires_at");
        builder.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_ai_usage_ledger_user_created_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ai_usage_ledger_user");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ai_usage_ledger_reviewed_by");
        builder.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.ChatMessageId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ai_usage_ledger_chat_message");
        builder.HasOne<KnowledgeIngestionJob>().WithMany().HasForeignKey(x => x.IngestionJobId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ai_usage_ledger_ingestion_job");
        builder.HasOne<KnowledgeDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ai_usage_ledger_version");
        builder.HasIndex(x => x.ChatMessageId).HasDatabaseName("ix_ai_usage_ledger_chat_message_id");
        builder.HasIndex(x => x.IngestionJobId).HasDatabaseName("ix_ai_usage_ledger_ingestion_job_id");
        builder.HasIndex(x => x.VersionId).HasDatabaseName("ix_ai_usage_ledger_version_id");
        builder.HasIndex(x => x.ReviewedByUserId).HasDatabaseName("ix_ai_usage_ledger_reviewed_by_user_id");
    }
}

/// <summary>Spending counters per UTC period; locked FOR UPDATE in (kind, start) order by every reservation and settlement.</summary>
public sealed class AiBudgetPeriodConfiguration : IEntityTypeConfiguration<AiBudgetPeriod>
{
    public void Configure(EntityTypeBuilder<AiBudgetPeriod> builder)
    {
        builder.ToTable("ai_budget_period", t =>
        {
            t.HasCheckConstraint("ck_ai_budget_period_kind", $"period_kind IN ({string.Join(", ", AiBudgetPeriodKinds.All.Select(k => $"'{k}'"))})");
            t.HasCheckConstraint("ck_ai_budget_period_non_negative",
                "reserved_usd >= 0 AND settled_usd >= 0 AND uncertain_usd >= 0 AND requests >= 0 AND blocked >= 0");
        });
        builder.HasKey(x => new { x.PeriodKind, x.PeriodStart });
        builder.Property(x => x.PeriodKind).HasColumnName("period_kind").HasMaxLength(20);
        builder.Property(x => x.PeriodStart).HasColumnName("period_start");
        builder.Property(x => x.ReservedUsd).HasColumnName("reserved_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.SettledUsd).HasColumnName("settled_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.UncertainUsd).HasColumnName("uncertain_usd").HasPrecision(14, 8).IsRequired();
        builder.Property(x => x.Requests).HasColumnName("requests").IsRequired();
        builder.Property(x => x.Blocked).HasColumnName("blocked").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Ignore(x => x.CommittedUsd);
    }
}
