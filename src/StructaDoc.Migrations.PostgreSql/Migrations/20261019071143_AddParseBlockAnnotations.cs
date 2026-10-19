using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddParseBlockAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "caption",
                table: "parse_blocks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "footnote",
                table: "parse_blocks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "caption",
                table: "parse_blocks");

            migrationBuilder.DropColumn(
                name: "footnote",
                table: "parse_blocks");
        }
    }
}
