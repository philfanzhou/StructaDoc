using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MigrateAccessGrantCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $access_grant_identity_preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM document_access_grants
                        WHERE octet_length(created_by) > 4096
                           OR octet_length(principal_issuer) > 512
                           OR octet_length(principal_issuer) <> char_length(principal_issuer)
                           OR octet_length(principal_subject) > 255
                           OR octet_length(principal_subject) <> char_length(principal_subject)
                    ) THEN
                        RAISE EXCEPTION 'Access Grant canonical identity migration refused: actor or principal source is non-ASCII or oversized.';
                    END IF;
                END
                $access_grant_identity_preflight$;
                """);

            migrationBuilder.DropIndex(name: "ux_document_access_grants_principal", table: "document_access_grants");
            migrationBuilder.AddColumn<byte[]>(name: "principal_issuer_binary", table: "document_access_grants", type: "bytea", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "principal_subject_binary", table: "document_access_grants", type: "bytea", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_issuer", table: "document_access_grants", type: "bytea", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_subject", table: "document_access_grants", type: "bytea", maxLength: 255, nullable: true);
            migrationBuilder.AddColumn<byte[]>(name: "created_by_legacy", table: "document_access_grants", type: "bytea", maxLength: 4096, nullable: true);
            migrationBuilder.Sql("""
                UPDATE document_access_grants
                SET created_by_legacy = convert_to(created_by, 'UTF8'),
                    principal_issuer_binary = convert_to(principal_issuer, 'UTF8'),
                    principal_subject_binary = convert_to(principal_subject, 'UTF8');
                """);
            migrationBuilder.DropColumn(name: "created_by", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_subject", table: "document_access_grants");
            migrationBuilder.RenameColumn(name: "principal_issuer_binary", table: "document_access_grants", newName: "principal_issuer");
            migrationBuilder.RenameColumn(name: "principal_subject_binary", table: "document_access_grants", newName: "principal_subject");
            migrationBuilder.AlterColumn<byte[]>(name: "principal_issuer", table: "document_access_grants", type: "bytea", maxLength: 512, nullable: false, oldClrType: typeof(byte[]), oldType: "bytea", oldMaxLength: 512, oldNullable: true);
            migrationBuilder.AlterColumn<byte[]>(name: "principal_subject", table: "document_access_grants", type: "bytea", maxLength: 255, nullable: false, oldClrType: typeof(byte[]), oldType: "bytea", oldMaxLength: 255, oldNullable: true);
            migrationBuilder.AddCheckConstraint(name: "ck_document_access_grants_created_by_state", table: "document_access_grants", sql: "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL AND created_by_legacy IS NOT NULL))");
            migrationBuilder.CreateIndex(name: "ux_document_access_grants_principal", table: "document_access_grants", columns: new[] { "document_id", "principal_issuer", "principal_subject" }, unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $access_grant_identity_down_preflight$
                BEGIN
                    IF EXISTS (SELECT 1 FROM document_access_grants WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL) THEN
                        RAISE EXCEPTION 'Access Grant canonical identity downgrade refused: canonical actor rows cannot be represented by the legacy scalar.';
                    END IF;
                END
                $access_grant_identity_down_preflight$;
                """);
            migrationBuilder.DropIndex(name: "ux_document_access_grants_principal", table: "document_access_grants");
            migrationBuilder.DropCheckConstraint(name: "ck_document_access_grants_created_by_state", table: "document_access_grants");
            migrationBuilder.AddColumn<string>(name: "created_by", table: "document_access_grants", type: "character varying(1024)", maxLength: 1024, nullable: true);
            migrationBuilder.AddColumn<string>(name: "principal_issuer_text", table: "document_access_grants", type: "character varying(512)", maxLength: 512, nullable: true);
            migrationBuilder.AddColumn<string>(name: "principal_subject_text", table: "document_access_grants", type: "character varying(255)", maxLength: 255, nullable: true);
            migrationBuilder.Sql("""
                UPDATE document_access_grants
                SET created_by = convert_from(created_by_legacy, 'UTF8'),
                    principal_issuer_text = convert_from(principal_issuer, 'UTF8'),
                    principal_subject_text = convert_from(principal_subject, 'UTF8');
                """);
            migrationBuilder.AlterColumn<string>(name: "created_by", table: "document_access_grants", type: "character varying(1024)", maxLength: 1024, nullable: false, oldClrType: typeof(string), oldType: "character varying(1024)", oldMaxLength: 1024, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "principal_issuer_text", table: "document_access_grants", type: "character varying(512)", maxLength: 512, nullable: false, oldClrType: typeof(string), oldType: "character varying(512)", oldMaxLength: 512, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "principal_subject_text", table: "document_access_grants", type: "character varying(255)", maxLength: 255, nullable: false, oldClrType: typeof(string), oldType: "character varying(255)", oldMaxLength: 255, oldNullable: true);
            migrationBuilder.DropColumn(name: "created_by_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "created_by_subject", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "created_by_legacy", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_issuer", table: "document_access_grants");
            migrationBuilder.DropColumn(name: "principal_subject", table: "document_access_grants");
            migrationBuilder.RenameColumn(name: "principal_issuer_text", table: "document_access_grants", newName: "principal_issuer");
            migrationBuilder.RenameColumn(name: "principal_subject_text", table: "document_access_grants", newName: "principal_subject");
            migrationBuilder.CreateIndex(name: "ux_document_access_grants_principal", table: "document_access_grants", columns: new[] { "document_id", "principal_issuer", "principal_subject" }, unique: true);
        }
    }
}
