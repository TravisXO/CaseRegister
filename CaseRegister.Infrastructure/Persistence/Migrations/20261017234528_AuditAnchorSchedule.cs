using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditAnchorSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "anchor_deferred",
                table: "audit_chain_head",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_anchor_at",
                table: "audit_chain_head",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "audit_chain_head",
                keyColumn: "id",
                keyValue: 1,
                column: "last_anchor_at",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "anchor_deferred",
                table: "audit_chain_head");

            migrationBuilder.DropColumn(
                name: "last_anchor_at",
                table: "audit_chain_head");
        }
    }
}
