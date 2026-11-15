using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IotEnergyMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "iot_device",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_version = table.Column<int>(type: "integer", nullable: false),
                    secret_rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sensor_model = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    max_current_amps = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iot_device", x => x.id);
                    table.CheckConstraint("ck_iot_device_disabled", "(status = 'DISABLED') = (disabled_at IS NOT NULL) AND (status <> 'DISABLED' OR (disabled_by_user_id IS NOT NULL AND disabled_reason IS NOT NULL))");
                    table.CheckConstraint("ck_iot_device_max_current", "max_current_amps > 0 AND max_current_amps <= 1000");
                    table.CheckConstraint("ck_iot_device_secret_version", "secret_version >= 1");
                    table.CheckConstraint("ck_iot_device_status", "status IN ('ACTIVE', 'DISABLED')");
                    table.ForeignKey(
                        name: "fk_iot_device_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_device_disabled_by",
                        column: x => x.disabled_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "iot_device_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    bound_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iot_device_binding", x => x.id);
                    table.CheckConstraint("ck_iot_device_binding_released", "(status = 'ACTIVE') = (released_at IS NULL) AND (status <> 'RELEASED' OR (released_by_user_id IS NOT NULL AND release_reason IS NOT NULL))");
                    table.CheckConstraint("ck_iot_device_binding_status", "status IN ('ACTIVE', 'RELEASED')");
                    table.ForeignKey(
                        name: "fk_iot_device_binding_bound_by",
                        column: x => x.bound_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_device_binding_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_device_binding_device",
                        column: x => x.device_id,
                        principalTable: "iot_device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_device_binding_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_device_binding_released_by",
                        column: x => x.released_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "iot_telemetry_reading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_amps = table.Column<double>(type: "double precision", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iot_telemetry_reading", x => x.id);
                    table.CheckConstraint("ck_iot_telemetry_reading_current", "current_amps BETWEEN -10000 AND 10000");
                    table.ForeignKey(
                        name: "fk_iot_telemetry_reading_binding",
                        column: x => x.binding_id,
                        principalTable: "iot_device_binding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_iot_telemetry_reading_device",
                        column: x => x.device_id,
                        principalTable: "iot_device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_iot_device_created_by_user_id",
                table: "iot_device",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_iot_device_disabled_by_user_id",
                table: "iot_device",
                column: "disabled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_iot_device_status",
                table: "iot_device",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_iot_device_device_id",
                table: "iot_device",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_iot_device_binding_bound_by_user_id",
                table: "iot_device_binding",
                column: "bound_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_iot_device_binding_customer",
                table: "iot_device_binding",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_iot_device_binding_project_bound_at",
                table: "iot_device_binding",
                columns: new[] { "project_id", "bound_at" });

            migrationBuilder.CreateIndex(
                name: "IX_iot_device_binding_released_by_user_id",
                table: "iot_device_binding",
                column: "released_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_iot_device_binding_active_device",
                table: "iot_device_binding",
                column: "device_id",
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "ux_iot_device_binding_active_project",
                table: "iot_device_binding",
                column: "project_id",
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "ix_iot_telemetry_reading_binding_received_at",
                table: "iot_telemetry_reading",
                columns: new[] { "binding_id", "received_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_iot_telemetry_reading_device_received_at",
                table: "iot_telemetry_reading",
                columns: new[] { "device_id", "received_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "iot_telemetry_reading");

            migrationBuilder.DropTable(
                name: "iot_device_binding");

            migrationBuilder.DropTable(
                name: "iot_device");
        }
    }
}
