using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Shared knowledge publication scope (2026-11-23). Additive and backward compatible with the previous API build (new nullable columns,
    /// a new audit table, a widened check). Cutover data rule (PostgreSQL only, idempotent):
    /// <list type="bullet">
    /// <item>A document PUBLISHED with legacy audience CUSTOMER was already readable by all five roles: its published version is approved
    /// for the shared scope, with an audit row MIGRATED_FROM_CUSTOMER_AUDIENCE (no actor).</item>
    /// <item>A document PUBLISHED with audience INTERNAL is NOT approved: it stays unretrievable for everyone until an Admin re-confirms it for
    /// all signed-in roles or unpublishes it (GET /api/admin/knowledge-documents/shared-visibility-review lists them). Nothing is deleted.</item>
    /// </list>
    /// Rollback (Down) restores the former audience behaviour; answers stored with the new scope become INTERNAL (withheld from customers).
    /// Back up knowledge_publication_approval before a rollback: Down drops it.
    /// </summary>
    public partial class SharedKnowledgePublicationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "shared_scope_approved_at",
                table: "knowledge_document_version",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shared_scope_approved_by_user_id",
                table: "knowledge_document_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_publication_approval",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    legacy_audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_publication_approval", x => x.id);
                    table.CheckConstraint("ck_knowledge_publication_approval_action", "action IN ('APPROVED_ALL_SIGNED_IN_ROLES', 'MIGRATED_FROM_CUSTOMER_AUDIENCE')");
                    table.CheckConstraint("ck_knowledge_publication_approval_actor", "actor_user_id IS NOT NULL OR action = 'MIGRATED_FROM_CUSTOMER_AUDIENCE'");
                    table.CheckConstraint("ck_knowledge_publication_approval_legacy_audience", "legacy_audience IN ('INTERNAL', 'CUSTOMER')");
                    table.ForeignKey(
                        name: "FK_knowledge_publication_approval_knowledge_document_document_~",
                        column: x => x.document_id,
                        principalTable: "knowledge_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_publication_approval_knowledge_document_version_v~",
                        column: x => x.version_id,
                        principalTable: "knowledge_document_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_publication_approval_user_account_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_shared_scope_approved_by_user_id",
                table: "knowledge_document_version",
                column: "shared_scope_approved_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_document_version_shared_scope",
                table: "knowledge_document_version",
                sql: "shared_scope_approved_by_user_id IS NULL OR shared_scope_approved_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message",
                sql: "retrieval_audience IS NULL OR retrieval_audience IN ('INTERNAL', 'CUSTOMER', 'ALL_SIGNED_IN_ROLES')");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_publication_approval_actor_user_id",
                table: "knowledge_publication_approval",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_publication_approval_document_created_at",
                table: "knowledge_publication_approval",
                columns: new[] { "document_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_publication_approval_version_id",
                table: "knowledge_publication_approval",
                column: "version_id");

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_document_version_user_account_shared_scope_approv~",
                table: "knowledge_document_version",
                column: "shared_scope_approved_by_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Carry over only what every role could already read (audience CUSTOMER); INTERNAL stays pending review. PostgreSQL (like every
            // migration here; SQLite test databases use EnsureCreated).
            migrationBuilder.Sql("""
                    INSERT INTO knowledge_publication_approval (id, document_id, version_id, action, legacy_audience, actor_user_id, note, created_at)
                    SELECT gen_random_uuid(), d.id, v.id, 'MIGRATED_FROM_CUSTOMER_AUDIENCE', d.audience, NULL,
                           'Cutover 2026-11-23: already readable by every signed-in role (legacy audience CUSTOMER).', now()
                    FROM knowledge_document d
                    JOIN knowledge_document_version v ON v.id = d.published_version_id AND v.document_id = d.id
                    WHERE d.status = 'PUBLISHED' AND d.audience = 'CUSTOMER' AND v.status = 'PUBLISHED' AND v.shared_scope_approved_at IS NULL;

                    UPDATE knowledge_document_version v
                    SET shared_scope_approved_at = now()
                    FROM knowledge_document d
                    WHERE v.id = d.published_version_id AND v.document_id = d.id
                      AND d.status = 'PUBLISHED' AND d.audience = 'CUSTOMER' AND v.status = 'PUBLISHED' AND v.shared_scope_approved_at IS NULL;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The former check does not know the shared scope: such answers become INTERNAL (withheld from customers, readable by staff owners).
            migrationBuilder.Sql("UPDATE chat_message SET retrieval_audience = 'INTERNAL' WHERE retrieval_audience = 'ALL_SIGNED_IN_ROLES';");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_document_version_user_account_shared_scope_approv~",
                table: "knowledge_document_version");

            migrationBuilder.DropTable(
                name: "knowledge_publication_approval");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_version_shared_scope_approved_by_user_id",
                table: "knowledge_document_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_document_version_shared_scope",
                table: "knowledge_document_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message");

            migrationBuilder.DropColumn(
                name: "shared_scope_approved_at",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "shared_scope_approved_by_user_id",
                table: "knowledge_document_version");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message",
                sql: "retrieval_audience IS NULL OR retrieval_audience IN ('INTERNAL', 'CUSTOMER')");
        }
    }
}
