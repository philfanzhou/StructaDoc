using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations;

/// <inheritdoc />
public partial class MigrateParseRunCanonicalActorIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $parse_run_identity_preflight$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM parse_runs
                    WHERE created_by IS NOT NULL AND octet_length(created_by) > 1024
                ) THEN
                    RAISE EXCEPTION 'Parse Run canonical actor migration refused: created_by exceeds 1024 UTF-8 bytes.';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM parse_runs
                    WHERE idempotency_key IS NOT NULL
                      AND (octet_length(idempotency_key) NOT BETWEEN 1 AND 256
                           OR idempotency_key !~ '^[!-~]+$')
                ) THEN
                    RAISE EXCEPTION 'Parse Run canonical actor migration refused: idempotency_key is not 1-256 visible ASCII bytes.';
                END IF;
            END
            $parse_run_identity_preflight$;
            """);

        migrationBuilder.AddColumn<byte[]>(name: "created_by_issuer", table: "parse_runs", type: "bytea", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<byte[]>(name: "created_by_legacy", table: "parse_runs", type: "bytea", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<byte[]>(name: "created_by_subject", table: "parse_runs", type: "bytea", maxLength: 255, nullable: true);
        migrationBuilder.Sql("""
            UPDATE parse_runs
            SET created_by_legacy = CASE
                WHEN created_by IS NULL THEN NULL
                ELSE convert_to(created_by, 'UTF8')
            END;
            """);

        migrationBuilder.DropIndex(name: "ux_parse_runs_idempotency", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by", table: "parse_runs");
        migrationBuilder.AlterColumn<string>(
            name: "idempotency_key",
            table: "parse_runs",
            type: "character varying(256)",
            unicode: false,
            maxLength: 256,
            nullable: true,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldUnicode: false,
            oldMaxLength: 256,
            oldNullable: true);
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
            DO $parse_run_identity_down_preflight$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM parse_runs
                    WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
                ) THEN
                    RAISE EXCEPTION 'Parse Run canonical actor downgrade refused: canonical rows cannot be represented by the legacy scalar.';
                END IF;
            END
            $parse_run_identity_down_preflight$;
            """);

        migrationBuilder.DropIndex(name: "ix_parse_runs_legacy_idempotency", table: "parse_runs");
        migrationBuilder.DropIndex(name: "ux_parse_runs_idempotency", table: "parse_runs");
        migrationBuilder.DropCheckConstraint(name: "ck_parse_runs_created_by_state", table: "parse_runs");
        migrationBuilder.AddColumn<string>(name: "created_by", table: "parse_runs", type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.Sql("""
            UPDATE parse_runs
            SET created_by = CASE
                WHEN created_by_legacy IS NULL THEN NULL
                ELSE convert_from(created_by_legacy, 'UTF8')
            END;
            """);
        migrationBuilder.DropColumn(name: "created_by_issuer", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by_legacy", table: "parse_runs");
        migrationBuilder.DropColumn(name: "created_by_subject", table: "parse_runs");
        migrationBuilder.AlterColumn<string>(
            name: "idempotency_key",
            table: "parse_runs",
            type: "character varying(256)",
            unicode: false,
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldUnicode: false,
            oldMaxLength: 256,
            oldNullable: true,
            oldCollation: "C");
        migrationBuilder.CreateIndex(
            name: "ux_parse_runs_idempotency",
            table: "parse_runs",
            columns: new[] { "created_by", "document_id", "idempotency_key" },
            unique: true);
    }
}
