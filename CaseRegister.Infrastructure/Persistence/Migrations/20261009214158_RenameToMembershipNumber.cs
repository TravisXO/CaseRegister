using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameToMembershipNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "agent_registration_number",
                table: "users",
                newName: "membership_number");

            migrationBuilder.RenameIndex(
                name: "ux_users_agent_registration_number",
                table: "users",
                newName: "ux_users_membership_number");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "membership_number",
                table: "users",
                newName: "agent_registration_number");

            migrationBuilder.RenameIndex(
                name: "ux_users_membership_number",
                table: "users",
                newName: "ux_users_agent_registration_number");
        }
    }
}
