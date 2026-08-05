using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddProviderSubmissionCheckpoint : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "protected_submission_continuation",
            table: "parse_runs",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "protected_submission_continuation",
            table: "parse_runs");
    }
}
