using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_authenticators_user_id",
                table: "user_authenticators");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "user_authenticators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    proposed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_refusal_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_refusal_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_refused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_actions", x => x.id);
                    table.CheckConstraint("ck_pending_actions_one_target", "(target_user_id IS NULL) <> (target_agency_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_pending_actions_agencies_target_agency_id",
                        column: x => x.target_agency_id,
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pending_actions_users_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pending_actions_users_proposed_by_user_id",
                        column: x => x.proposed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pending_actions_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_user_authenticators_live",
                table: "user_authenticators",
                column: "user_id",
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_decided_by_user_id",
                table: "pending_actions",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_proposed_by_user_id",
                table: "pending_actions",
                column: "proposed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_status_expires_at",
                table: "pending_actions",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_target_agency_id",
                table: "pending_actions",
                column: "target_agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_target_user_id",
                table: "pending_actions",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_pending_actions_one_pending_per_target",
                table: "pending_actions",
                columns: new[] { "type", "target_user_id", "target_agency_id" },
                unique: true,
                filter: "status = 'Pending'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_actions");

            migrationBuilder.DropIndex(
                name: "ux_user_authenticators_live",
                table: "user_authenticators");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "user_authenticators");

            migrationBuilder.CreateIndex(
                name: "ix_user_authenticators_user_id",
                table: "user_authenticators",
                column: "user_id",
                unique: true);
        }
    }
}
