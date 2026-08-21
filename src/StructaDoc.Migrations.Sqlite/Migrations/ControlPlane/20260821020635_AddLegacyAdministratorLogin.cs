using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddLegacyAdministratorLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_normalized_login",
                table: "admin_users",
                type: "TEXT",
                unicode: false,
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_admin_users_legacy_normalized_login",
                table: "admin_users",
                column: "legacy_normalized_login",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_admin_users_legacy_normalized_login",
                table: "admin_users");

            migrationBuilder.DropColumn(
                name: "legacy_normalized_login",
                table: "admin_users");
        }
    }
}
