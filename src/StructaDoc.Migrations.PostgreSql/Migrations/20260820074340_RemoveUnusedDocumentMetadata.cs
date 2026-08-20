using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedDocumentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "metadata_json",
                table: "documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "metadata_json",
                table: "documents",
                type: "text",
                nullable: true);
        }
    }
}
