using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations;

/// <inheritdoc />
public partial class AddProviderConfigsAndParseCreation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_configs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                provider_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                is_enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                default_marker = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                current_version_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                updated_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                concurrency_version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_configs", x => x.id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "provider_config_versions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                provider_config_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                version_number = table.Column<int>(type: "int", nullable: false),
                base_url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                model = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                backend = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                protected_credential = table.Column<string>(type: "varchar(8192)", maxLength: 8192, nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_config_versions", x => x.id);
                table.ForeignKey(
                    name: "FK_provider_config_versions_provider_configs_provider_config_id",
                    column: x => x.provider_config_id,
                    principalTable: "provider_configs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "ux_parse_runs_idempotency",
            table: "parse_runs",
            columns: new[] { "created_by", "document_id", "idempotency_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_provider_config_versions_number",
            table: "provider_config_versions",
            columns: new[] { "provider_config_id", "version_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_provider_configs_current_version",
            table: "provider_configs",
            column: "current_version_id");

        migrationBuilder.CreateIndex(
            name: "ux_provider_configs_default_marker",
            table: "provider_configs",
            column: "default_marker",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "provider_config_versions");

        migrationBuilder.DropTable(
            name: "provider_configs");

        migrationBuilder.DropIndex(
            name: "ux_parse_runs_idempotency",
            table: "parse_runs");
    }
}
