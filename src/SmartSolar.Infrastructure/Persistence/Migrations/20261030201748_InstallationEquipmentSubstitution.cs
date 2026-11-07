using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstallationEquipmentSubstitution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installation_equipment_substitution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    manager_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manager_reason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    manager_approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_declined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_decline_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_equipment_substitution", x => x.id);
                    table.CheckConstraint("ck_installation_equipment_substitution_approved", "status <> 'APPROVED' OR customer_approved_at IS NOT NULL");
                    table.CheckConstraint("ck_installation_equipment_substitution_customer", "(customer_approved_at IS NULL OR customer_declined_at IS NULL) AND ((customer_approved_at IS NULL AND customer_declined_at IS NULL) = (customer_user_id IS NULL))");
                    table.CheckConstraint("ck_installation_equipment_substitution_declined", "status <> 'CUSTOMER_DECLINED' OR customer_declined_at IS NOT NULL");
                    table.CheckConstraint("ck_installation_equipment_substitution_status", "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED', 'CUSTOMER_DECLINED', 'SUPERSEDED')");
                    table.CheckConstraint("ck_installation_equipment_substitution_superseded", "(status = 'SUPERSEDED') = (superseded_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_installation_equipment_substitution_installation_project_pr~",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_manager",
                        column: x => x.manager_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_quotation",
                        column: x => x.quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_equipment_substitution_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    substitution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    contracted_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    installed_quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_equipment_substitution_item", x => x.id);
                    table.CheckConstraint("ck_installation_equipment_substitution_item_quantities", "contracted_quantity >= 0 AND installed_quantity >= 0 AND contracted_quantity <> installed_quantity");
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_item_product",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_equipment_substitution_item_substitution",
                        column: x => x.substitution_id,
                        principalTable: "installation_equipment_substitution",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installation_equipment_substitution_contract_id",
                table: "installation_equipment_substitution",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_equipment_substitution_customer_user_id",
                table: "installation_equipment_substitution",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_equipment_substitution_manager_user_id",
                table: "installation_equipment_substitution",
                column: "manager_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_equipment_substitution_project_created_at",
                table: "installation_equipment_substitution",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_equipment_substitution_quotation_id",
                table: "installation_equipment_substitution",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_equipment_substitution_active",
                table: "installation_equipment_substitution",
                column: "project_id",
                unique: true,
                filter: "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED')");

            migrationBuilder.CreateIndex(
                name: "IX_installation_equipment_substitution_item_product_id",
                table: "installation_equipment_substitution_item",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_equipment_substitution_item_substitution",
                table: "installation_equipment_substitution_item",
                column: "substitution_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installation_equipment_substitution_item");

            migrationBuilder.DropTable(
                name: "installation_equipment_substitution");
        }
    }
}
