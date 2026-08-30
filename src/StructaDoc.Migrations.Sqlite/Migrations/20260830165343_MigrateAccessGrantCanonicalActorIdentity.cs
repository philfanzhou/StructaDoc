using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MigrateAccessGrantCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _structadoc_access_grant_identity_preflight (valid INTEGER NOT NULL, CONSTRAINT ck_structadoc_access_grants_require_utf8 CHECK (valid = 1));
                INSERT INTO _structadoc_access_grant_identity_preflight(valid) SELECT CASE WHEN encoding = 'UTF-8' THEN 1 ELSE 0 END FROM pragma_encoding;
                INSERT INTO _structadoc_access_grant_identity_preflight(valid)
                SELECT 0 WHERE EXISTS (
                    SELECT 1 FROM document_access_grants
                    WHERE typeof(created_by) <> 'text' OR length(CAST(created_by AS BLOB)) > 4096
                       OR typeof(principal_issuer) <> 'text' OR length(CAST(principal_issuer AS BLOB)) > 512
                       OR typeof(principal_subject) <> 'text' OR length(CAST(principal_subject AS BLOB)) > 255);

                WITH RECURSIVE source(row_key, bytes) AS (
                    SELECT id, CAST(created_by AS BLOB) FROM document_access_grants
                ), scan(row_key, bytes, byte_count, position, valid) AS (
                    SELECT row_key, bytes, length(bytes), 1, 1 FROM source
                    UNION ALL
                    SELECT row_key, bytes, byte_count,
                        CASE
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F' THEN position + 1
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'C2' AND 'DF' THEN position + 2
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'E0' AND 'EF' THEN position + 3
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'F0' AND 'F4' THEN position + 4
                            ELSE position END,
                        CASE WHEN
                            substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F'
                            OR (substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'C2' AND 'DF' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'E0' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN 'A0' AND 'BF' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR ((substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'E1' AND 'EC' OR substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'EE' AND 'EF') AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'ED' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND '9F' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'F0' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '90' AND 'BF' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF' AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'F1' AND 'F3' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF' AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'F4' AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND '8F' AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF' AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                        THEN 1 ELSE 0 END
                    FROM scan WHERE valid = 1 AND position <= byte_count)
                INSERT INTO _structadoc_access_grant_identity_preflight(valid) SELECT 0 FROM scan WHERE valid = 0 LIMIT 1;

                WITH RECURSIVE principal_source(row_key, bytes) AS (
                    SELECT id || ':issuer', CAST(principal_issuer AS BLOB) FROM document_access_grants
                    UNION ALL SELECT id || ':subject', CAST(principal_subject AS BLOB) FROM document_access_grants
                ), principal_scan(row_key, bytes, byte_count, position, valid) AS (
                    SELECT row_key, bytes, length(bytes), 1, 1 FROM principal_source
                    UNION ALL SELECT row_key, bytes, byte_count, position + 1,
                        CASE WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F' THEN 1 ELSE 0 END
                    FROM principal_scan WHERE valid = 1 AND position <= byte_count)
                INSERT INTO _structadoc_access_grant_identity_preflight(valid) SELECT 0 FROM principal_scan WHERE valid = 0 LIMIT 1;
                DROP TABLE _structadoc_access_grant_identity_preflight;
                """);

            migrationBuilder.Sql("""
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE _document_access_grants_canonical_new (
                    id TEXT NOT NULL CONSTRAINT PK_document_access_grants PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    principal_issuer BLOB NOT NULL,
                    principal_subject BLOB NOT NULL,
                    permissions INTEGER NOT NULL,
                    created_by_issuer BLOB NULL,
                    created_by_subject BLOB NULL,
                    created_by_legacy BLOB NULL,
                    created_at_utc TEXT NOT NULL,
                    CONSTRAINT ck_document_access_grants_created_by_state CHECK ((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL AND created_by_legacy IS NOT NULL)),
                    CONSTRAINT FK_document_access_grants_documents_document_id FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE CASCADE);
                INSERT INTO _document_access_grants_canonical_new (id, document_id, principal_issuer, principal_subject, permissions, created_by_issuer, created_by_subject, created_by_legacy, created_at_utc)
                SELECT id, document_id, CAST(principal_issuer AS BLOB), CAST(principal_subject AS BLOB), permissions, NULL, NULL, CAST(created_by AS BLOB), created_at_utc FROM document_access_grants;
                DROP INDEX ux_document_access_grants_principal;
                DROP TABLE document_access_grants;
                ALTER TABLE _document_access_grants_canonical_new RENAME TO document_access_grants;
                CREATE UNIQUE INDEX ux_document_access_grants_principal ON document_access_grants (document_id, principal_issuer, principal_subject);
                COMMIT;
                PRAGMA foreign_keys = ON;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _structadoc_access_grant_identity_down (valid INTEGER NOT NULL, CONSTRAINT ck_structadoc_access_grant_identity_downgrade CHECK (valid = 1));
                INSERT INTO _structadoc_access_grant_identity_down(valid) SELECT 0 WHERE EXISTS (SELECT 1 FROM document_access_grants WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL);
                DROP TABLE _structadoc_access_grant_identity_down;
                """);
            migrationBuilder.Sql("""
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE _document_access_grants_legacy_new (
                    id TEXT NOT NULL CONSTRAINT PK_document_access_grants PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    principal_issuer TEXT NOT NULL,
                    principal_subject TEXT NOT NULL,
                    permissions INTEGER NOT NULL,
                    created_by TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    CONSTRAINT FK_document_access_grants_documents_document_id FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE CASCADE);
                INSERT INTO _document_access_grants_legacy_new (id, document_id, principal_issuer, principal_subject, permissions, created_by, created_at_utc)
                SELECT id, document_id, CAST(principal_issuer AS TEXT), CAST(principal_subject AS TEXT), permissions, CAST(created_by_legacy AS TEXT), created_at_utc FROM document_access_grants;
                DROP INDEX ux_document_access_grants_principal;
                DROP TABLE document_access_grants;
                ALTER TABLE _document_access_grants_legacy_new RENAME TO document_access_grants;
                CREATE UNIQUE INDEX ux_document_access_grants_principal ON document_access_grants (document_id, principal_issuer, principal_subject);
                COMMIT;
                PRAGMA foreign_keys = ON;
                """, suppressTransaction: true);
        }
    }
}
