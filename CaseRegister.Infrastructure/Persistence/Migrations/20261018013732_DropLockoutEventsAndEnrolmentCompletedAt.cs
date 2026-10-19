using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLockoutEventsAndEnrolmentCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing is hard-deleted: the lockout_events table was never written to, and this refuses to drop it if that has changed.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM lockout_events) THEN
                        RAISE EXCEPTION 'lockout_events holds rows; it will not be dropped until someone has decided what to do with them.';
                    END IF;
                END
                $$;
                """);

            // The two columns were always set together, at the moment enrolment finished. used_at is kept; make sure it holds that moment.
            migrationBuilder.Sql("UPDATE enrolment_codes SET used_at = completed_at WHERE used_at IS NULL AND completed_at IS NOT NULL;");

            migrationBuilder.DropTable(
                name: "lockout_events");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "enrolment_codes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "enrolment_codes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("UPDATE enrolment_codes SET completed_at = used_at;");

            migrationBuilder.CreateTable(
                name: "lockout_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lockout_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_lockout_events_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lockout_events_user_id_created_at",
                table: "lockout_events",
                columns: new[] { "user_id", "created_at" });
        }
    }
}
