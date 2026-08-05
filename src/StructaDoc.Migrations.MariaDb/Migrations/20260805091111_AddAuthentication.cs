using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations;

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
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                email = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                normalized_email = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                display_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                password_hash = table.Column<string>(type: "varchar(1024)", unicode: false, maxLength: 1024, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                security_stamp = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                last_login_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_admin_users", x => x.id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "api_clients",
            columns: table => new
            {
                id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                secret_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                scopes = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4"),
                is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                revoked_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_clients", x => x.id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

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
