using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Cloud RAG cost control (2026-11-17). Additive: cost-control columns on rag_configuration (defaults = the initial policy, so existing rows
    /// and the previous API build stay valid), reserved_usd / client_message_id on rag_usage_reservation (defaults keep older inserts valid),
    /// the ai_usage_ledger and ai_budget_period tables. On a database that has the RAG service's knowledge_chunk table, its unique key becomes
    /// (version_id, embedding_model, chunk_index) so the chunks of a new embedding model can be written next to the old model's chunks during a
    /// reindex (no row is changed; the previous RAG service keeps working: it deletes and writes its own chunks per version).
    /// </summary>
    public partial class CloudRagCostControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_message_id",
                table: "rag_usage_reservation",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "reserved_usd",
                table: "rag_usage_reservation",
                type: "numeric(14,8)",
                precision: 14,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "daily_budget_usd",
                table: "rag_configuration",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 1.00m);

            migrationBuilder.AddColumn<bool>(
                name: "generation_enabled",
                table: "rag_configuration",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "generation_timeout_seconds",
                table: "rag_configuration",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "global_concurrency",
                table: "rag_configuration",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "max_question_characters",
                table: "rag_configuration",
                type: "integer",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<decimal>(
                name: "monthly_budget_usd",
                table: "rag_configuration",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 4.00m);

            migrationBuilder.AddColumn<decimal>(
                name: "monthly_ingestion_budget_usd",
                table: "rag_configuration",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0.50m);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_effort",
                table: "rag_configuration",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "user_concurrency",
                table: "rag_configuration",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "user_requests_per_day",
                table: "rag_configuration",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "ai_budget_period",
                columns: table => new
                {
                    period_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    reserved_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    settled_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    uncertain_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    blocked = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_budget_period", x => new { x.period_kind, x.period_start });
                    table.CheckConstraint("ck_ai_budget_period_kind", "period_kind IN ('DAY', 'MONTH', 'INGEST_MONTH')");
                    table.CheckConstraint("ck_ai_budget_period_non_negative", "reserved_usd >= 0 AND settled_usd >= 0 AND uncertain_usd >= 0 AND requests >= 0 AND blocked >= 0");
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    chat_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ingestion_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    config_version = table.Column<int>(type: "integer", nullable: true),
                    embedding_provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    embedding_tokens = table.Column<long>(type: "bigint", nullable: false),
                    embedding_cost_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    generation_provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    generation_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    reasoning_tokens = table.Column<long>(type: "bigint", nullable: false),
                    generation_cost_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    usage_reported = table.Column<bool>(type: "boolean", nullable: false),
                    reserved_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,8)", precision: 14, scale: 8, nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_ledger", x => x.id);
                    table.CheckConstraint("ck_ai_usage_ledger_lease", "expires_at > created_at");
                    table.CheckConstraint("ck_ai_usage_ledger_non_negative", "embedding_tokens >= 0 AND input_tokens >= 0 AND cached_input_tokens >= 0 AND output_tokens >= 0 AND reasoning_tokens >= 0 AND embedding_cost_usd >= 0 AND generation_cost_usd >= 0 AND reserved_usd >= 0 AND cost_usd >= 0 AND cached_input_tokens <= input_tokens");
                    table.CheckConstraint("ck_ai_usage_ledger_operation", "operation IN ('CHAT', 'INGESTION')");
                    table.CheckConstraint("ck_ai_usage_ledger_outcome", "outcome IN ('PENDING', 'ANSWERED', 'INSUFFICIENT_CONTEXT', 'PROVIDER_ERROR', 'PROVIDER_RATE_LIMITED', 'TIMEOUT', 'BUSY', 'EXPIRED', 'INDEXED', 'INGEST_FAILED', 'REVIEWED')");
                    table.CheckConstraint("ck_ai_usage_ledger_review", "(reviewed_at IS NULL) = (reviewed_by_user_id IS NULL)");
                    table.CheckConstraint("ck_ai_usage_ledger_settled", "(status = 'RESERVED') = (settled_at IS NULL)");
                    table.CheckConstraint("ck_ai_usage_ledger_status", "status IN ('RESERVED', 'SETTLED', 'UNCERTAIN', 'RELEASED')");
                    table.ForeignKey(
                        name: "fk_ai_usage_ledger_chat_message",
                        column: x => x.chat_message_id,
                        principalTable: "chat_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_usage_ledger_ingestion_job",
                        column: x => x.ingestion_job_id,
                        principalTable: "knowledge_ingestion_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_usage_ledger_reviewed_by",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_usage_ledger_user",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_usage_ledger_version",
                        column: x => x.version_id,
                        principalTable: "knowledge_document_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_rag_usage_reservation_client_message",
                table: "rag_usage_reservation",
                columns: new[] { "user_id", "client_message_id" },
                unique: true,
                filter: "client_message_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rag_usage_reservation_usd",
                table: "rag_usage_reservation",
                sql: "reserved_usd >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rag_configuration_budgets",
                table: "rag_configuration",
                sql: "daily_budget_usd >= 0 AND daily_budget_usd <= 10 AND monthly_budget_usd >= 0 AND monthly_budget_usd <= 100 AND monthly_ingestion_budget_usd >= 0 AND monthly_ingestion_budget_usd <= 10 AND daily_budget_usd <= monthly_budget_usd");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rag_configuration_quotas",
                table: "rag_configuration",
                sql: "user_requests_per_day BETWEEN 1 AND 200 AND user_concurrency BETWEEN 1 AND 2 AND global_concurrency BETWEEN 1 AND 10 AND max_question_characters BETWEEN 100 AND 2000 AND generation_timeout_seconds BETWEEN 5 AND 60");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rag_configuration_reasoning_effort",
                table: "rag_configuration",
                sql: "reasoning_effort IS NULL OR reasoning_effort IN ('none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_chat_message_id",
                table: "ai_usage_ledger",
                column: "chat_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_created_at",
                table: "ai_usage_ledger",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_ingestion_job_id",
                table: "ai_usage_ledger",
                column: "ingestion_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_reviewed_by_user_id",
                table: "ai_usage_ledger",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_status_expires_at",
                table: "ai_usage_ledger",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_user_created_at",
                table: "ai_usage_ledger",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_ledger_version_id",
                table: "ai_usage_ledger",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ux_ai_usage_ledger_idempotency_key",
                table: "ai_usage_ledger",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('knowledge_chunk') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_knowledge_chunk_version_index' AND conrelid = 'knowledge_chunk'::regclass) THEN
                            ALTER TABLE knowledge_chunk DROP CONSTRAINT ux_knowledge_chunk_version_index;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_knowledge_chunk_version_model_index' AND conrelid = 'knowledge_chunk'::regclass) THEN
                            ALTER TABLE knowledge_chunk ADD CONSTRAINT ux_knowledge_chunk_version_model_index UNIQUE (version_id, embedding_model, chunk_index);
                        END IF;
                    END IF;
                END $$;
                """);
        }

        /// <summary>
        /// Refused, changing nothing, while the cost ledger holds rows (financial history is never dropped) or a knowledge version has chunks
        /// of two embedding models (the previous unique key could not be restored). Otherwise the previous schema is restored exactly.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ai_usage_ledger) OR EXISTS (SELECT 1 FROM ai_budget_period) THEN
                        RAISE EXCEPTION 'CloudRagCostControl rollback refused: the AI cost ledger holds rows (financial history). Export them first; nothing was changed.';
                    END IF;
                    IF to_regclass('knowledge_chunk') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM knowledge_chunk GROUP BY version_id, chunk_index HAVING count(*) > 1) THEN
                            RAISE EXCEPTION 'CloudRagCostControl rollback refused: knowledge versions hold chunks of two embedding models. Finish or undo the reindex first; nothing was changed.';
                        END IF;
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_knowledge_chunk_version_model_index' AND conrelid = 'knowledge_chunk'::regclass) THEN
                            ALTER TABLE knowledge_chunk DROP CONSTRAINT ux_knowledge_chunk_version_model_index;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_knowledge_chunk_version_index' AND conrelid = 'knowledge_chunk'::regclass) THEN
                            ALTER TABLE knowledge_chunk ADD CONSTRAINT ux_knowledge_chunk_version_index UNIQUE (version_id, chunk_index);
                        END IF;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "ai_budget_period");

            migrationBuilder.DropTable(
                name: "ai_usage_ledger");

            migrationBuilder.DropIndex(
                name: "ux_rag_usage_reservation_client_message",
                table: "rag_usage_reservation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rag_usage_reservation_usd",
                table: "rag_usage_reservation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rag_configuration_budgets",
                table: "rag_configuration");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rag_configuration_quotas",
                table: "rag_configuration");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rag_configuration_reasoning_effort",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "client_message_id",
                table: "rag_usage_reservation");

            migrationBuilder.DropColumn(
                name: "reserved_usd",
                table: "rag_usage_reservation");

            migrationBuilder.DropColumn(
                name: "daily_budget_usd",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "generation_enabled",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "generation_timeout_seconds",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "global_concurrency",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "max_question_characters",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "monthly_budget_usd",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "monthly_ingestion_budget_usd",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "reasoning_effort",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "user_concurrency",
                table: "rag_configuration");

            migrationBuilder.DropColumn(
                name: "user_requests_per_day",
                table: "rag_configuration");
        }
    }
}
