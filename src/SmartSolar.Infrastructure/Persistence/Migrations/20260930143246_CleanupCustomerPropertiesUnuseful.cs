using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CleanupCustomerPropertiesUnuseful : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_customer_user_account_assigned_sale_id",
                table: "customer");

            migrationBuilder.DropIndex(
                name: "IX_customer_assigned_sale_id",
                table: "customer");

            migrationBuilder.DropColumn(
                name: "assigned_sale_id",
                table: "customer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assigned_sale_id",
                table: "customer",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_assigned_sale_id",
                table: "customer",
                column: "assigned_sale_id");

            migrationBuilder.AddForeignKey(
                name: "FK_customer_user_account_assigned_sale_id",
                table: "customer",
                column: "assigned_sale_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
