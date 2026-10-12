using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationsAndStaffInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_pending_actions_one_pending_per_target",
                table: "pending_actions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pending_actions_one_target",
                table: "pending_actions");

            migrationBuilder.AddColumn<string>(
                name: "target_email",
                table: "pending_actions",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "member_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_number = table.Column<int>(type: "integer", nullable: false),
                    membership_class = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    agency_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    certificate_number = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    flags = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reapplication_count = table.Column<int>(type: "integer", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    approved_agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_applications_agencies_approved_agency_id",
                        column: x => x.approved_agency_id,
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_member_applications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_setup_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_setup_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_setup_tokens_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_staff_setup_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pending_actions_one_pending_per_target",
                table: "pending_actions",
                columns: new[] { "type", "target_user_id", "target_agency_id", "target_email" },
                unique: true,
                filter: "status = 'Pending'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_pending_actions_one_target",
                table: "pending_actions",
                sql: "num_nonnulls(target_user_id, target_agency_id, target_email) = 1");

            migrationBuilder.CreateIndex(
                name: "ix_member_applications_approved_agency_id",
                table: "member_applications",
                column: "approved_agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_applications_membership_number",
                table: "member_applications",
                column: "membership_number");

            migrationBuilder.CreateIndex(
                name: "ix_member_applications_status",
                table: "member_applications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_member_applications_user_id",
                table: "member_applications",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_setup_tokens_created_by_user_id",
                table: "staff_setup_tokens",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_setup_tokens_token_hash",
                table: "staff_setup_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_setup_tokens_user_id",
                table: "staff_setup_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_applications");

            migrationBuilder.DropTable(
                name: "staff_setup_tokens");

            migrationBuilder.DropIndex(
                name: "ux_pending_actions_one_pending_per_target",
                table: "pending_actions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pending_actions_one_target",
                table: "pending_actions");

            migrationBuilder.DropColumn(
                name: "target_email",
                table: "pending_actions");

            migrationBuilder.CreateIndex(
                name: "ux_pending_actions_one_pending_per_target",
                table: "pending_actions",
                columns: new[] { "type", "target_user_id", "target_agency_id" },
                unique: true,
                filter: "status = 'Pending'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_pending_actions_one_target",
                table: "pending_actions",
                sql: "(target_user_id IS NULL) <> (target_agency_id IS NULL)");
        }
    }
}
