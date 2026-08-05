using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations;

/// <inheritdoc />
public partial class AddCanonicalParseResults : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "provider_metadata_json",
            table: "parse_runs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "result_schema_version",
            table: "parse_runs",
            type: "character varying(16)",
            unicode: false,
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "result_sha256",
            table: "parse_runs",
            type: "character varying(64)",
            unicode: false,
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "parse_artifacts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                media_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                sha256 = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                storage_ref = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                metadata_json = table.Column<string>(type: "text", nullable: true),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_parse_artifacts", x => x.id);
                table.ForeignKey(
                    name: "FK_parse_artifacts_parse_runs_parse_run_id",
                    column: x => x.parse_run_id,
                    principalTable: "parse_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "parse_assets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                media_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                sha256 = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                storage_ref = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                width = table.Column<int>(type: "integer", nullable: true),
                height = table.Column<int>(type: "integer", nullable: true),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_parse_assets", x => x.id);
                table.UniqueConstraint("AK_parse_assets_parse_run_id_id", x => new { x.parse_run_id, x.id });
                table.ForeignKey(
                    name: "FK_parse_assets_parse_runs_parse_run_id",
                    column: x => x.parse_run_id,
                    principalTable: "parse_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "parse_pages",
            columns: table => new
            {
                parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                number = table.Column<int>(type: "integer", nullable: false),
                width = table.Column<double>(type: "double precision", nullable: true),
                height = table.Column<double>(type: "double precision", nullable: true),
                unit = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: true),
                source_locator_json = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_parse_pages", x => new { x.parse_run_id, x.number });
                table.ForeignKey(
                    name: "FK_parse_pages_parse_runs_parse_run_id",
                    column: x => x.parse_run_id,
                    principalTable: "parse_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "parse_blocks",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<int>(type: "integer", nullable: false),
                page_number = table.Column<int>(type: "integer", nullable: true),
                type = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                subtype = table.Column<string>(type: "character varying(100)", unicode: false, maxLength: 100, nullable: true),
                content = table.Column<string>(type: "text", nullable: true),
                content_format = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: true),
                bbox_x0 = table.Column<double>(type: "double precision", nullable: true),
                bbox_y0 = table.Column<double>(type: "double precision", nullable: true),
                bbox_x1 = table.Column<double>(type: "double precision", nullable: true),
                bbox_y1 = table.Column<double>(type: "double precision", nullable: true),
                confidence = table.Column<double>(type: "double precision", nullable: true),
                asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_locator_json = table.Column<string>(type: "text", nullable: true),
                provider_data_json = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_parse_blocks", x => x.id);
                table.ForeignKey(
                    name: "FK_parse_blocks_parse_assets_parse_run_id_asset_id",
                    columns: x => new { x.parse_run_id, x.asset_id },
                    principalTable: "parse_assets",
                    principalColumns: new[] { "parse_run_id", "id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_parse_blocks_parse_pages_parse_run_id_page_number",
                    columns: x => new { x.parse_run_id, x.page_number },
                    principalTable: "parse_pages",
                    principalColumns: new[] { "parse_run_id", "number" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_parse_blocks_parse_runs_parse_run_id",
                    column: x => x.parse_run_id,
                    principalTable: "parse_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_parse_artifacts_sha256",
            table: "parse_artifacts",
            column: "sha256");

        migrationBuilder.CreateIndex(
            name: "ux_parse_artifacts_key",
            table: "parse_artifacts",
            columns: new[] { "parse_run_id", "type", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_parse_assets_name",
            table: "parse_assets",
            columns: new[] { "parse_run_id", "name" });

        migrationBuilder.CreateIndex(
            name: "ix_parse_assets_sha256",
            table: "parse_assets",
            column: "sha256");

        migrationBuilder.CreateIndex(
            name: "ix_parse_blocks_asset",
            table: "parse_blocks",
            columns: new[] { "parse_run_id", "asset_id" });

        migrationBuilder.CreateIndex(
            name: "ix_parse_blocks_page",
            table: "parse_blocks",
            columns: new[] { "parse_run_id", "page_number" });

        migrationBuilder.CreateIndex(
            name: "ux_parse_blocks_sequence",
            table: "parse_blocks",
            columns: new[] { "parse_run_id", "sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "parse_artifacts");

        migrationBuilder.DropTable(
            name: "parse_blocks");

        migrationBuilder.DropTable(
            name: "parse_assets");

        migrationBuilder.DropTable(
            name: "parse_pages");

        migrationBuilder.DropColumn(
            name: "provider_metadata_json",
            table: "parse_runs");

        migrationBuilder.DropColumn(
            name: "result_schema_version",
            table: "parse_runs");

        migrationBuilder.DropColumn(
            name: "result_sha256",
            table: "parse_runs");
    }
}
