using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations;

/// <inheritdoc />
public partial class InitialPersistence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "documents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                original_file_name = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                media_type = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                extension = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                storage_ref = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_by = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                metadata_json = table.Column<string>(type: "longtext", nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_documents", x => x.id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "parse_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                document_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                stage = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                provider_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                provider_config_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                provider_config_version = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                options_json = table.Column<string>(type: "longtext", nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                source_media_type = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                submitted_media_type = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                conversion_json = table.Column<string>(type: "longtext", nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                external_task_id = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                attempt_count = table.Column<int>(type: "int", nullable: false),
                max_attempts = table.Column<int>(type: "int", nullable: false),
                next_attempt_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                error_code = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                error_message = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_by = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                idempotency_key = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                claimed_by = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                lease_expires_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                started_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                completed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                concurrency_version = table.Column<long>(type: "bigint", nullable: false)
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
            })
            .Annotation("MySql:CharSet", "utf8mb4");

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
