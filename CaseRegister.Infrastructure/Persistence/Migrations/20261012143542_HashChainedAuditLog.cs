using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseRegister.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HashChainedAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The temporary table stays exactly as it was, under a new name. The application brings its rows into the
            // chain, in order, the first time it needs the chain (it needs the HMAC key for that, which a migration does not have).
            migrationBuilder.Sql("ALTER TABLE audit_log RENAME TO audit_log_legacy;");
            migrationBuilder.Sql("ALTER TABLE audit_log_legacy RENAME CONSTRAINT pk_audit_log TO pk_audit_log_legacy;");
            migrationBuilder.Sql("ALTER INDEX ix_audit_log_created_at RENAME TO ix_audit_log_legacy_created_at;");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    before_json = table.Column<string>(type: "text", nullable: true),
                    after_json = table.Column<string>(type: "text", nullable: true),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entry_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_chain_head",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_anchor_date = table.Column<DateOnly>(type: "date", nullable: true),
                    legacy_imported = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_chain_head", x => x.id);
                    table.CheckConstraint("ck_audit_chain_head_single_row", "id = 1");
                });

            migrationBuilder.InsertData(
                table: "audit_chain_head",
                columns: new[] { "id", "last_anchor_date", "last_hash", "last_sequence", "legacy_imported" },
                values: new object[] { 1, null, "0000000000000000000000000000000000000000000000000000000000000000", 0L, false });

            migrationBuilder.CreateIndex(name: "ix_audit_log_action", table: "audit_log", column: "action");
            migrationBuilder.CreateIndex(name: "ix_audit_log_actor_user_id", table: "audit_log", column: "actor_user_id");
            migrationBuilder.CreateIndex(name: "ix_audit_log_agency_id", table: "audit_log", column: "agency_id");
            migrationBuilder.CreateIndex(name: "ix_audit_log_occurred_at", table: "audit_log", column: "occurred_at");
            migrationBuilder.CreateIndex(name: "ix_audit_log_sequence", table: "audit_log", column: "sequence", unique: true);

            // Append-only for everyone, the database owner included, until someone deliberately disables the trigger.
            // The application role is also refused by its grants; this is the second lock.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit_log_refuse_changes() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'The audit log is append-only: % is not allowed on %', TG_OP, TG_TABLE_NAME USING ERRCODE = '42501';
                END
                $$;
                """);
            foreach (var table in new[] { "audit_log", "audit_log_legacy" })
            {
                migrationBuilder.Sql($"CREATE TRIGGER {table}_no_update_delete BEFORE UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION audit_log_refuse_changes();");
                migrationBuilder.Sql($"CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table} FOR EACH STATEMENT EXECUTE FUNCTION audit_log_refuse_changes();");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_legacy_no_update_delete ON audit_log_legacy;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_legacy_no_truncate ON audit_log_legacy;");
            migrationBuilder.DropTable(name: "audit_chain_head");
            migrationBuilder.DropTable(name: "audit_log");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_log_refuse_changes();");
            migrationBuilder.Sql("ALTER INDEX ix_audit_log_legacy_created_at RENAME TO ix_audit_log_created_at;");
            migrationBuilder.Sql("ALTER TABLE audit_log_legacy RENAME CONSTRAINT pk_audit_log_legacy TO pk_audit_log;");
            migrationBuilder.Sql("ALTER TABLE audit_log_legacy RENAME TO audit_log;");
        }
    }
}
