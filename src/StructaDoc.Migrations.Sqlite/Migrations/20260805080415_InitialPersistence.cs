using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class InitialPersistence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "documents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                original_file_name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                media_type = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                sha256 = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                storage_ref = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                created_by = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                metadata_json = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_documents", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "parse_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                status = table.Column<string>(type: "TEXT", unicode: false, maxLength: 32, nullable: false),
                stage = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true),
                provider_type = table.Column<string>(type: "TEXT", unicode: false, maxLength: 100, nullable: false),
                provider_config_id = table.Column<Guid>(type: "TEXT", nullable: false),
                provider_config_version = table.Column<Guid>(type: "TEXT", nullable: false),
                options_json = table.Column<string>(type: "TEXT", nullable: false),
                source_media_type = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                submitted_media_type = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                conversion_json = table.Column<string>(type: "TEXT", nullable: true),
                external_task_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                max_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                next_attempt_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                error_code = table.Column<string>(type: "TEXT", unicode: false, maxLength: 128, nullable: true),
                error_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                created_by = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                idempotency_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                claimed_by = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                lease_expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                started_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                completed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                concurrency_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_parse_runs", x => x.id);
                table.ForeignKey(
                    name: "FK_parse_runs_documents_document_id",
                    column: x => x.document_id,
                    principalTable: "documents",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_documents_sha256",
            table: "documents",
            column: "sha256");

        migrationBuilder.CreateIndex(
            name: "ix_parse_runs_document_id",
            table: "parse_runs",
            column: "document_id");

        migrationBuilder.CreateIndex(
            name: "ix_parse_runs_due",
            table: "parse_runs",
            columns: new[] { "status", "next_attempt_at_utc" });

        migrationBuilder.CreateIndex(
            name: "ix_parse_runs_lease_expiry",
            table: "parse_runs",
            column: "lease_expires_at_utc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "parse_runs");

        migrationBuilder.DropTable(
            name: "documents");
    }
}
