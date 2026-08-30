using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations;

/// <inheritdoc />
public partial class MigrateParseRunCanonicalActorIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE _structadoc_parse_run_identity_preflight (
                valid TINYINT NOT NULL,
                CONSTRAINT ck_structadoc_parse_run_identity_source CHECK (valid = 1)
            );
            INSERT INTO _structadoc_parse_run_identity_preflight(valid)
            SELECT 0 WHERE EXISTS (
                SELECT 1 FROM parse_runs
                WHERE (created_by IS NOT NULL AND OCTET_LENGTH(created_by) > 1024)
                   OR (idempotency_key IS NOT NULL AND (
                        OCTET_LENGTH(idempotency_key) NOT BETWEEN 1 AND 256
                        OR idempotency_key NOT REGEXP '^[!-~]+$'))
            );
            DROP TEMPORARY TABLE _structadoc_parse_run_identity_preflight;
            ALTER TABLE parse_runs ROW_FORMAT=DYNAMIC;
            CREATE TEMPORARY TABLE _structadoc_parse_run_row_format_preflight (
                valid TINYINT NOT NULL,
                CONSTRAINT ck_structadoc_parse_runs_require_dynamic_rows CHECK (valid = 1)
            );
            INSERT INTO _structadoc_parse_run_row_format_preflight(valid)
            SELECT CASE WHEN UPPER(ROW_FORMAT) = 'DYNAMIC' THEN 1 ELSE 0 END
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'parse_runs';
            DROP TEMPORARY TABLE _structadoc_parse_run_row_format_preflight;
            """);

        migrationBuilder.AddColumn<byte[]>(name: "created_by_issuer", table: "parse_runs", type: "varbinary(512)", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<byte[]>(name: "created_by_legacy", table: "parse_runs", type: "varbinary(1024)", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<byte[]>(name: "created_by_subject", table: "parse_runs", type: "varbinary(255)", maxLength: 255, nullable: true);
        migrationBuilder.Sql("""
            UPDATE parse_runs
            SET created_by_legacy = CASE
                WHEN created_by IS NULL THEN NULL
                ELSE CONVERT(created_by USING binary)
            END;
            """);

        migrationBuilder.DropIndex(name: "ux_parse_runs_idempotency", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by", table: "parse_runs");
        migrationBuilder.AlterColumn<string>(
                name: "idempotency_key",
                table: "parse_runs",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true,
                collation: "ascii_bin",
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldUnicode: false,
                oldMaxLength: 256,
                oldNullable: true)
            .Annotation("MySql:CharSet", "ascii")
            .OldAnnotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.AddCheckConstraint(
            name: "ck_parse_runs_created_by_state",
            table: "parse_runs",
            sql: "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL))");
        migrationBuilder.CreateIndex(
            name: "ux_parse_runs_idempotency",
            table: "parse_runs",
            columns: new[] { "created_by_issuer", "created_by_subject", "document_id", "idempotency_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_parse_runs_legacy_idempotency",
            table: "parse_runs",
            columns: new[] { "document_id", "idempotency_key" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE _structadoc_parse_run_identity_down (
                valid TINYINT NOT NULL,
                CONSTRAINT ck_structadoc_parse_run_identity_downgrade CHECK (valid = 1)
            );
            INSERT INTO _structadoc_parse_run_identity_down(valid)
            SELECT 0 WHERE EXISTS (
                SELECT 1 FROM parse_runs
                WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
            );
            DROP TEMPORARY TABLE _structadoc_parse_run_identity_down;
            """);

        migrationBuilder.DropIndex(name: "ix_parse_runs_legacy_idempotency", table: "parse_runs");
        migrationBuilder.DropIndex(name: "ux_parse_runs_idempotency", table: "parse_runs");
        migrationBuilder.DropCheckConstraint(name: "ck_parse_runs_created_by_state", table: "parse_runs");
        migrationBuilder.AddColumn<string>(
                name: "created_by",
                table: "parse_runs",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.Sql("""
            UPDATE parse_runs
            SET created_by = CASE
                WHEN created_by_legacy IS NULL THEN NULL
                ELSE CONVERT(created_by_legacy USING utf8mb4)
            END;
            """);
        migrationBuilder.DropColumn(name: "created_by_issuer", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by_legacy", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by_subject", table: "parse_runs");
        migrationBuilder.AlterColumn<string>(
                name: "idempotency_key",
                table: "parse_runs",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldUnicode: false,
                oldMaxLength: 256,
                oldNullable: true,
                oldCollation: "ascii_bin")
            .Annotation("MySql:CharSet", "utf8mb4")
            .OldAnnotation("MySql:CharSet", "ascii");
        migrationBuilder.CreateIndex(
            name: "ux_parse_runs_idempotency",
            table: "parse_runs",
            columns: new[] { "created_by", "document_id", "idempotency_key" },
            unique: true);
    }
}

