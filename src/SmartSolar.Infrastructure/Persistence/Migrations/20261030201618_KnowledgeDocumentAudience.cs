using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeDocumentAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audience",
                table: "knowledge_document",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "INTERNAL");

            migrationBuilder.AddColumn<string>(
                name: "retrieval_audience",
                table: "chat_message",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_document_audience_change",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document_audience_change", x => x.id);
                    table.CheckConstraint("ck_knowledge_document_audience_change_values", "from_audience IN ('INTERNAL', 'CUSTOMER') AND to_audience IN ('INTERNAL', 'CUSTOMER') AND from_audience <> to_audience");
                    table.ForeignKey(
                        name: "FK_knowledge_document_audience_change_knowledge_document_docum~",
                        column: x => x.document_id,
                        principalTable: "knowledge_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_document_audience_change_user_account_actor_user_~",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_document_status_audience",
                table: "knowledge_document",
                columns: new[] { "status", "audience" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_document_audience",
                table: "knowledge_document",
                sql: "audience IN ('INTERNAL', 'CUSTOMER')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message",
                sql: "retrieval_audience IS NULL OR retrieval_audience IN ('INTERNAL', 'CUSTOMER')");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_audience_change_actor_user_id",
                table: "knowledge_document_audience_change",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_document_audience_change_document_created_at",
                table: "knowledge_document_audience_change",
                columns: new[] { "document_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_document_audience_change");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_document_status_audience",
                table: "knowledge_document");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_document_audience",
                table: "knowledge_document");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_message_retrieval_audience",
                table: "chat_message");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "knowledge_document");

            migrationBuilder.DropColumn(
                name: "retrieval_audience",
                table: "chat_message");
        }
    }
}
