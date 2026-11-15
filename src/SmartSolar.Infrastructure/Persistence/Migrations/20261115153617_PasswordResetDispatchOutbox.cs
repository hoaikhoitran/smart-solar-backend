using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PasswordResetDispatchOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "password_reset_dispatch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reset_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_dispatch", x => x.id);
                    table.CheckConstraint("ck_password_reset_dispatch_attempts", "publish_attempts >= 0");
                    table.CheckConstraint("ck_password_reset_dispatch_status", "status IN ('PENDING', 'PUBLISHED', 'SENT', 'EXPIRED')");
                    table.CheckConstraint("ck_password_reset_dispatch_url", "(status = 'PENDING') = (reset_url IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_password_reset_dispatch_action_token",
                        column: x => x.action_token_id,
                        principalTable: "auth_action_token",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_password_reset_dispatch_user",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_dispatch_status_next_attempt",
                table: "password_reset_dispatch",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_dispatch_user",
                table: "password_reset_dispatch",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_password_reset_dispatch_action_token",
                table: "password_reset_dispatch",
                column: "action_token_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_dispatch");
        }
    }
}
