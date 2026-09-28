using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceProductImageFileIdWithImageUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_file_id",
                table: "product");

            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "product",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_url",
                table: "product");

            migrationBuilder.AddColumn<Guid>(
                name: "image_file_id",
                table: "product",
                type: "uuid",
                nullable: true);
        }
    }
}
