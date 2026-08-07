using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddUserWorkspaceLifecycleAndSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at_utc",
                table: "parse_runs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                table: "parse_runs",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "active")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at_utc",
                table: "documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                table: "documents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "active")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "owner_issuer",
                table: "documents",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "owner_subject",
                table: "documents",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "cleanup_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    target_type = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    storage_refs_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attempt_count = table.Column<int>(type: "int", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    error_message = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    concurrency_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanup_jobs", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "document_access_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    document_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    principal_issuer = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    principal_subject = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    permissions = table.Column<int>(type: "int", nullable: false),
                    created_by = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_access_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_access_grants_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "parse_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    parse_run_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    segment_index = table.Column<int>(type: "int", nullable: false),
                    start_page = table.Column<int>(type: "int", nullable: false),
                    end_page = table.Column<int>(type: "int", nullable: false),
                    storage_ref = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    external_task_id = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protected_submission_continuation = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parse_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_parse_segments_parse_runs_parse_run_id",
                        column: x => x.parse_run_id,
                        principalTable: "parse_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_documents_owner_created_at",
                table: "documents",
                columns: new[] { "owner_issuer", "owner_subject", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cleanup_jobs_due",
                table: "cleanup_jobs",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_cleanup_jobs_target",
                table: "cleanup_jobs",
                columns: new[] { "target_type", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_access_grants_principal",
                table: "document_access_grants",
                columns: new[] { "document_id", "principal_issuer", "principal_subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_parse_segments_index",
                table: "parse_segments",
                columns: new[] { "parse_run_id", "segment_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleanup_jobs");

            migrationBuilder.DropTable(
                name: "document_access_grants");

            migrationBuilder.DropTable(
                name: "parse_segments");

            migrationBuilder.DropIndex(
                name: "ix_documents_owner_created_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at_utc",
                table: "parse_runs");

            migrationBuilder.DropColumn(
                name: "lifecycle_state",
                table: "parse_runs");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at_utc",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "lifecycle_state",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "owner_issuer",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "owner_subject",
                table: "documents");
        }
    }
}
