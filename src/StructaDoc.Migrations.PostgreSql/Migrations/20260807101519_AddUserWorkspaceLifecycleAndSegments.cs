using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations
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
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                table: "parse_runs",
                type: "character varying(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at_utc",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                table: "documents",
                type: "character varying(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<string>(
                name: "owner_issuer",
                table: "documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_subject",
                table: "documents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cleanup_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_refs_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    concurrency_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanup_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_access_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    principal_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    permissions = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "parse_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    start_page = table.Column<int>(type: "integer", nullable: false),
                    end_page = table.Column<int>(type: "integer", nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: false),
                    external_task_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    protected_submission_continuation = table.Column<string>(type: "text", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                });

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
