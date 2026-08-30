using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class MigrateDocumentCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE _structadoc_document_identity_preflight (
                    valid TINYINT NOT NULL,
                    CONSTRAINT ck_structadoc_document_identity_source CHECK (valid = 1)
                );
                INSERT INTO _structadoc_document_identity_preflight(valid)
                SELECT 0 WHERE EXISTS (
                    SELECT 1 FROM documents
                    WHERE (created_by IS NOT NULL AND OCTET_LENGTH(created_by) > 1024)
                       OR ((owner_issuer IS NULL) <> (owner_subject IS NULL))
                       OR (owner_issuer IS NOT NULL AND (
                            OCTET_LENGTH(owner_issuer) > 512
                            OR OCTET_LENGTH(owner_subject) > 255))
                );
                DROP TEMPORARY TABLE _structadoc_document_identity_preflight;
                ALTER TABLE documents ROW_FORMAT=DYNAMIC;
                CREATE TEMPORARY TABLE _structadoc_document_row_format_preflight (
                    valid TINYINT NOT NULL,
                    CONSTRAINT ck_structadoc_documents_require_dynamic_rows CHECK (valid = 1)
                );
                INSERT INTO _structadoc_document_row_format_preflight(valid)
                SELECT CASE WHEN UPPER(ROW_FORMAT) = 'DYNAMIC' THEN 1 ELSE 0 END
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'documents';
                DROP TEMPORARY TABLE _structadoc_document_row_format_preflight;
                """);

            migrationBuilder.DropIndex(name: "ix_documents_owner_created_at", table: "documents");
            migrationBuilder.AddColumn<byte[]>(name: "owner_issuer_binary", table: "documents", type: "varbinary(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "owner_subject_binary", table: "documents", type: "varbinary(255)", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_legacy", table: "documents", type: "varbinary(1024)", maxLength: 1024, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_issuer", table: "documents", type: "varbinary(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_subject", table: "documents", type: "varbinary(255)", maxLength: 255, nullable: true);

            migrationBuilder.Sql("""
                UPDATE documents
                SET created_by_legacy = CASE WHEN created_by IS NULL THEN NULL ELSE CONVERT(created_by USING binary) END,
                    owner_issuer_binary = CASE WHEN owner_issuer IS NULL THEN NULL ELSE CONVERT(owner_issuer USING binary) END,
                    owner_subject_binary = CASE WHEN owner_subject IS NULL THEN NULL ELSE CONVERT(owner_subject USING binary) END;
                """);

            migrationBuilder.DropColumn(name: "created_by", table: "documents");
            migrationBuilder.DropColumn(name: "owner_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "owner_subject", table: "documents");
            migrationBuilder.RenameColumn(name: "owner_issuer_binary", table: "documents", newName: "owner_issuer");
            migrationBuilder.RenameColumn(name: "owner_subject_binary", table: "documents", newName: "owner_subject");
            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_created_by_state",
                table: "documents",
                sql: "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL))");
            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_owner_state",
                table: "documents",
                sql: "((owner_issuer IS NULL AND owner_subject IS NULL) OR (owner_issuer IS NOT NULL AND owner_subject IS NOT NULL))");
            migrationBuilder.CreateIndex(name: "ix_documents_owner_created_at", table: "documents", columns: new[] { "owner_issuer", "owner_subject", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE _structadoc_document_identity_down_preflight (
                    valid TINYINT NOT NULL,
                    CONSTRAINT ck_structadoc_document_identity_downgrade CHECK (valid = 1)
                );
                INSERT INTO _structadoc_document_identity_down_preflight(valid)
                SELECT 0 WHERE EXISTS (
                    SELECT 1 FROM documents
                    WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
                );
                DROP TEMPORARY TABLE _structadoc_document_identity_down_preflight;
                """);

            migrationBuilder.DropIndex(name: "ix_documents_owner_created_at", table: "documents");
            migrationBuilder.DropCheckConstraint(name: "ck_documents_created_by_state", table: "documents");
            migrationBuilder.DropCheckConstraint(name: "ck_documents_owner_state", table: "documents");
            migrationBuilder.AddColumn<string>(name: "created_by", table: "documents", type: "varchar(255)", maxLength: 255, nullable: true).Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(name: "owner_issuer_text", table: "documents", type: "varchar(512)", maxLength: 512, nullable: true, collation: "ascii_bin").Annotation("MySql:CharSet", "ascii");
            migrationBuilder.AddColumn<string>(name: "owner_subject_text", table: "documents", type: "varchar(255)", maxLength: 255, nullable: true, collation: "ascii_bin").Annotation("MySql:CharSet", "ascii");
            migrationBuilder.Sql("""
                UPDATE documents
                SET created_by = CASE WHEN created_by_legacy IS NULL THEN NULL ELSE CONVERT(created_by_legacy USING utf8mb4) END,
                    owner_issuer_text = CASE WHEN owner_issuer IS NULL THEN NULL ELSE CONVERT(owner_issuer USING ascii) END,
                    owner_subject_text = CASE WHEN owner_subject IS NULL THEN NULL ELSE CONVERT(owner_subject USING ascii) END;
                """);
            migrationBuilder.DropColumn(name: "created_by_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "created_by_legacy", table: "documents");
            migrationBuilder.DropColumn(name: "created_by_subject", table: "documents");
            migrationBuilder.DropColumn(name: "owner_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "owner_subject", table: "documents");
            migrationBuilder.RenameColumn(name: "owner_issuer_text", table: "documents", newName: "owner_issuer");
            migrationBuilder.RenameColumn(name: "owner_subject_text", table: "documents", newName: "owner_subject");
            migrationBuilder.CreateIndex(name: "ix_documents_owner_created_at", table: "documents", columns: new[] { "owner_issuer", "owner_subject", "created_at_utc" });
        }
    }
}
