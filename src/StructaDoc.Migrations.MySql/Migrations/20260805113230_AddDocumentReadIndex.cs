using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MySql.Migrations;

/// <inheritdoc />
public partial class AddDocumentReadIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_documents_created_at_id",
            table: "documents",
            columns: new[] { "created_at_utc", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_documents_created_at_id",
            table: "documents");
    }
}
