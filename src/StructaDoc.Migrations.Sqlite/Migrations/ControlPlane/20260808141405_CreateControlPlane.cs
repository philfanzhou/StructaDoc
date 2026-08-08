using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class CreateControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    normalized_username = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", unicode: false, maxLength: 1024, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    security_stamp = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_login_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setup_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    administrator_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    claimed_from_address = table.Column<string>(type: "TEXT", unicode: false, maxLength: 45, nullable: false),
                    claimed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    acknowledged_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_claims", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_admin_users_normalized_username",
                table: "admin_users",
                column: "normalized_username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "setup_claims");
        }
    }
}
