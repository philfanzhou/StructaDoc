using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class MigrateAccessGrantCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE _structadoc_access_grant_identity_preflight (valid TINYINT NOT NULL, CONSTRAINT ck_structadoc_access_grant_identity_source CHECK (valid = 1));
                INSERT INTO _structadoc_access_grant_identity_preflight(valid)
                SELECT 0 WHERE EXISTS (SELECT 1 FROM document_access_grants WHERE OCTET_LENGTH(created_by) > 4096 OR OCTET_LENGTH(principal_issuer) > 512 OR OCTET_LENGTH(principal_subject) > 255);
                DROP TEMPORARY TABLE _structadoc_access_grant_identity_preflight;
                ALTER TABLE document_access_grants ROW_FORMAT=DYNAMIC;
                CREATE TEMPORARY TABLE _structadoc_access_grant_row_format (valid TINYINT NOT NULL, CONSTRAINT ck_structadoc_access_grants_require_dynamic_rows CHECK (valid = 1));
                INSERT INTO _structadoc_access_grant_row_format(valid)
                SELECT CASE WHEN UPPER(ROW_FORMAT) = 'DYNAMIC' THEN 1 ELSE 0 END FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'document_access_grants';
                DROP TEMPORARY TABLE _structadoc_access_grant_row_format;
                """);
            migrationBuilder.CreateIndex(name: "ix_document_access_grants_document_id_migration", table: "document_access_grants", column: "document_id");
            migrationBuilder.DropIndex(name: "ux_document_access_grants_principal", table: "document_access_grants");
            migrationBuilder.AddColumn<byte[]>(name: "principal_issuer_binary", table: "document_access_grants", type: "varbinary(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "principal_subject_binary", table: "document_access_grants", type: "varbinary(255)", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_issuer", table: "document_access_grants", type: "varbinary(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_subject", table: "document_access_grants", type: "varbinary(255)", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_legacy", table: "document_access_grants", type: "varbinary(4096)", maxLength: 4096, nullable: true);
            migrationBuilder.Sql("""
                UPDATE document_access_grants
                SET created_by_legacy = CONVERT(created_by USING binary),
                    principal_issuer_binary = CONVERT(principal_issuer USING binary),
                    principal_subject_binary = CONVERT(principal_subject USING binary);
                """);
            migrationBuilder.DropColumn(name: "created_by", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_subject", table: "document_access_grants");
            migrationBuilder.RenameColumn(name: "principal_issuer_binary", table: "document_access_grants", newName: "principal_issuer");
            migrationBuilder.RenameColumn(name: "principal_subject_binary", table: "document_access_grants", newName: "principal_subject");
            migrationBuilder.AlterColumn<byte[]>(name: "principal_issuer", table: "document_access_grants", type: "varbinary(512)", maxLength: 512, nullable: false, oldClrType: typeof(byte[]), oldType: "varbinary(512)", oldMaxLength: 512, oldNullable: true);
            migrationBuilder.AlterColumn<byte[]>(name: "principal_subject", table: "document_access_grants", type: "varbinary(255)", maxLength: 255, nullable: false, oldClrType: typeof(byte[]), oldType: "varbinary(255)", oldMaxLength: 255, oldNullable: true);
            migrationBuilder.AddCheckConstraint(name: "ck_document_access_grants_created_by_state", table: "document_access_grants", sql: "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL AND created_by_legacy IS NOT NULL))");
            migrationBuilder.CreateIndex(name: "ux_document_access_grants_principal", table: "document_access_grants", columns: new[] { "document_id", "principal_issuer", "principal_subject" }, unique: true);
            migrationBuilder.DropIndex(name: "ix_document_access_grants_document_id_migration", table: "document_access_grants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE _structadoc_access_grant_identity_down (valid TINYINT NOT NULL, CONSTRAINT ck_structadoc_access_grant_identity_downgrade CHECK (valid = 1));
                INSERT INTO _structadoc_access_grant_identity_down(valid) SELECT 0 WHERE EXISTS (SELECT 1 FROM document_access_grants WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL);
                DROP TEMPORARY TABLE _structadoc_access_grant_identity_down;
                """);
            migrationBuilder.CreateIndex(name: "ix_document_access_grants_document_id_migration", table: "document_access_grants", column: "document_id");
            migrationBuilder.DropIndex(name: "ux_document_access_grants_principal", table: "document_access_grants");
            migrationBuilder.DropCheckConstraint(name: "ck_document_access_grants_created_by_state", table: "document_access_grants");
            migrationBuilder.AddColumn<string>(name: "created_by", table: "document_access_grants", type: "varchar(1024)", maxLength: 1024, nullable: true).Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "principal_issuer_text", table: "document_access_grants", type: "varchar(512)", maxLength: 512, nullable: true, collation: "ascii_bin").Annotation("MySql:CharSet", "ascii");
            migrationBuilder.AddColumn<string>(name: "principal_subject_text", table: "document_access_grants", type: "varchar(255)", maxLength: 255, nullable: true, collation: "ascii_bin").Annotation("MySql:CharSet", "ascii");
            migrationBuilder.Sql("""
                UPDATE document_access_grants SET created_by = CONVERT(created_by_legacy USING utf8mb4), principal_issuer_text = CONVERT(principal_issuer USING ascii), principal_subject_text = CONVERT(principal_subject USING ascii);
                """);
            migrationBuilder.AlterColumn<string>(name: "created_by", table: "document_access_grants", type: "varchar(1024)", maxLength: 1024, nullable: false, oldClrType: typeof(string), oldType: "varchar(1024)", oldMaxLength: 1024, oldNullable: true).Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AlterColumn<string>(name: "principal_issuer_text", table: "document_access_grants", type: "varchar(512)", maxLength: 512, nullable: false, collation: "ascii_bin", oldClrType: typeof(string), oldType: "varchar(512)", oldMaxLength: 512, oldNullable: true).Annotation("MySql:CharSet", "ascii");
            migrationBuilder.AlterColumn<string>(name: "principal_subject_text", table: "document_access_grants", type: "varchar(255)", maxLength: 255, nullable: false, collation: "ascii_bin", oldClrType: typeof(string), oldType: "varchar(255)", oldMaxLength: 255, oldNullable: true).Annotation("MySql:CharSet", "ascii");
            migrationBuilder.DropColumn(name: "created_by_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "created_by_subject", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "created_by_legacy", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_subject", table: "document_access_grants");
            migrationBuilder.RenameColumn(name: "principal_issuer_text", table: "document_access_grants", newName: "principal_issuer");
            migrationBuilder.RenameColumn(name: "principal_subject_text", table: "document_access_grants", newName: "principal_subject");
            migrationBuilder.CreateIndex(name: "ux_document_access_grants_principal", table: "document_access_grants", columns: new[] { "document_id", "principal_issuer", "principal_subject" }, unique: true);
            migrationBuilder.DropIndex(name: "ix_document_access_grants_document_id_migration", table: "document_access_grants");
        }
    }
}
