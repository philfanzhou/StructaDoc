using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MigrateDocumentCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $document_identity_preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM documents
                        WHERE created_by IS NOT NULL AND octet_length(created_by) > 1024
                    ) THEN
                        RAISE EXCEPTION 'Document canonical actor migration refused: created_by exceeds 1024 UTF-8 bytes.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM documents
                        WHERE (owner_issuer IS NULL) <> (owner_subject IS NULL)
                           OR (owner_issuer IS NOT NULL AND (
                                octet_length(owner_issuer) > 512
                                OR octet_length(owner_issuer) <> char_length(owner_issuer)
                                OR octet_length(owner_subject) > 255
                                OR octet_length(owner_subject) <> char_length(owner_subject)))
                    ) THEN
                        RAISE EXCEPTION 'Document canonical actor migration refused: owner identity is incomplete, non-ASCII, or oversized.';
                    END IF;
                END
                $document_identity_preflight$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_documents_owner_created_at",
                table: "documents");

            migrationBuilder.AddColumn<byte[]>(
                name: "owner_issuer_binary",
                table: "documents",
                type: "bytea",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "owner_subject_binary",
                table: "documents",
                type: "bytea",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "created_by_legacy",
                table: "documents",
                type: "bytea",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "created_by_issuer",
                table: "documents",
                type: "bytea",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "created_by_subject",
                table: "documents",
                type: "bytea",
                maxLength: 255,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE documents
                SET created_by_legacy = CASE
                        WHEN created_by IS NULL THEN NULL
                        ELSE convert_to(created_by, 'UTF8')
                    END,
                    owner_issuer_binary = CASE
                        WHEN owner_issuer IS NULL THEN NULL
                        ELSE convert_to(owner_issuer, 'UTF8')
                    END,
                    owner_subject_binary = CASE
                        WHEN owner_subject IS NULL THEN NULL
                        ELSE convert_to(owner_subject, 'UTF8')
                    END;
                """);

            migrationBuilder.DropColumn(name: "created_by", table: "documents");
            migrationBuilder.DropColumn(name: "owner_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "owner_subject", table: "documents");

            migrationBuilder.RenameColumn(
                name: "owner_issuer_binary",
                table: "documents",
                newName: "owner_issuer");
            migrationBuilder.RenameColumn(
                name: "owner_subject_binary",
                table: "documents",
                newName: "owner_subject");

            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_created_by_state",
                table: "documents",
                sql: "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_owner_state",
                table: "documents",
                sql: "((owner_issuer IS NULL AND owner_subject IS NULL) OR (owner_issuer IS NOT NULL AND owner_subject IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "ix_documents_owner_created_at",
                table: "documents",
                columns: new[] { "owner_issuer", "owner_subject", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $document_identity_down_preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM documents
                        WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Document canonical actor downgrade refused: canonical audit rows cannot be represented by the legacy scalar.';
                    END IF;
                END
                $document_identity_down_preflight$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_documents_owner_created_at",
                table: "documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_documents_created_by_state",
                table: "documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_documents_owner_state",
                table: "documents");

            migrationBuilder.AddColumn<string>(name: "created_by", table: "documents", type: "character varying(255)", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<string>(name: "owner_issuer_text", table: "documents", type: "character varying(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<string>(name: "owner_subject_text", table: "documents", type: "character varying(255)", maxLength: 255, nullable: true);

            migrationBuilder.Sql("""
                UPDATE documents
                SET created_by = CASE WHEN created_by_legacy IS NULL THEN NULL ELSE convert_from(created_by_legacy, 'UTF8') END,
                    owner_issuer_text = CASE WHEN owner_issuer IS NULL THEN NULL ELSE convert_from(owner_issuer, 'UTF8') END,
                    owner_subject_text = CASE WHEN owner_subject IS NULL THEN NULL ELSE convert_from(owner_subject, 'UTF8') END;
                """);

            migrationBuilder.DropColumn(name: "created_by_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "created_by_legacy", table: "documents");
            migrationBuilder.DropColumn(name: "created_by_subject", table: "documents");
            migrationBuilder.DropColumn(name: "owner_issuer", table: "documents");
            migrationBuilder.DropColumn(name: "owner_subject", table: "documents");
            migrationBuilder.RenameColumn(name: "owner_issuer_text", table: "documents", newName: "owner_issuer");
            migrationBuilder.RenameColumn(name: "owner_subject_text", table: "documents", newName: "owner_subject");

            migrationBuilder.CreateIndex(
                name: "ix_documents_owner_created_at",
                table: "documents",
                columns: new[] { "owner_issuer", "owner_subject", "created_at_utc" });
        }
    }
}
