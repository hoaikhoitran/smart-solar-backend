using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-11-18: installation intervals offered by the responsible technician for the customer to choose. Additive only (one new table, no
    /// change to existing rows or columns), so an API build older than this release keeps working against the migrated schema. Manual SQL applies
    /// the Supabase hardening policy for new tables (RLS on without policies, no grants to the client roles). Down refuses while offers exist,
    /// so no history is ever dropped by a rollback.
    /// </summary>
    public partial class InstallationSlotOffers : Migration
    {
        public const string UpManualSql = """
            ALTER TABLE public.installation_slot_offer ENABLE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    REVOKE ALL ON public.installation_slot_offer FROM anon;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    REVOKE ALL ON public.installation_slot_offer FROM authenticated;
                END IF;
            END $$;
            """;

        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM installation_slot_offer) THEN
                    RAISE EXCEPTION 'installation_slot_offer has rows: export them and decide before rolling back (nothing was changed)';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installation_slot_offer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    selected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    selected_schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_slot_offer", x => x.id);
                    table.CheckConstraint("ck_installation_slot_offer_interval", "ends_at > starts_at");
                    table.CheckConstraint("ck_installation_slot_offer_selected", "(status = 'SELECTED') = (selected_at IS NOT NULL AND selected_schedule_id IS NOT NULL)");
                    table.CheckConstraint("ck_installation_slot_offer_status", "status IN ('OPEN', 'SELECTED', 'WITHDRAWN')");
                    table.CheckConstraint("ck_installation_slot_offer_withdrawn", "(status = 'WITHDRAWN') = (withdrawn_at IS NOT NULL AND withdrawn_reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_installation_slot_offer_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_slot_offer_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_slot_offer_selected_schedule",
                        column: x => x.selected_schedule_id,
                        principalTable: "installation_schedule_proposal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_slot_offer_technician",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_installation_slot_offer_contract_status",
                table: "installation_slot_offer",
                columns: new[] { "contract_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_slot_offer_created_by_user_id",
                table: "installation_slot_offer",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_slot_offer_technician_status",
                table: "installation_slot_offer",
                columns: new[] { "technician_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_installation_slot_offer_open_interval",
                table: "installation_slot_offer",
                columns: new[] { "contract_id", "starts_at", "ends_at" },
                unique: true,
                filter: "status = 'OPEN'");

            migrationBuilder.CreateIndex(
                name: "ux_installation_slot_offer_selected_schedule",
                table: "installation_slot_offer",
                column: "selected_schedule_id",
                unique: true,
                filter: "selected_schedule_id IS NOT NULL");

            migrationBuilder.Sql(UpManualSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.DropTable(
                name: "installation_slot_offer");
        }
    }
}
