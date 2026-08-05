using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "admin_users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                normalized_email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
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
            name: "api_clients",
            columns: table => new
            {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                secret_hash = table.Column<byte[]>(type: "BLOB", fixedLength: true, maxLength: 32, nullable: false),
                scopes = table.Column<string>(type: "TEXT", unicode: false, maxLength: 512, nullable: false),
                is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                revoked_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_clients", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ux_admin_users_normalized_email",
            table: "admin_users",
            column: "normalized_email",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "admin_users");

        migrationBuilder.DropTable(
            name: "api_clients");
    }
}
