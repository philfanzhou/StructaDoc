using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MigrateDocumentCanonicalActorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _structadoc_document_identity_preflight (
                    valid INTEGER NOT NULL,
                    CONSTRAINT ck_structadoc_documents_require_utf8 CHECK (valid = 1)
                );
                INSERT INTO _structadoc_document_identity_preflight(valid)
                SELECT CASE WHEN encoding = 'UTF-8' THEN 1 ELSE 0 END
                FROM pragma_encoding;
                INSERT INTO _structadoc_document_identity_preflight(valid)
                SELECT 0 WHERE EXISTS (
                    SELECT 1 FROM documents
                    WHERE (created_by IS NOT NULL AND (
                            typeof(created_by) <> 'text'
                            OR length(CAST(created_by AS BLOB)) > 1024))
                       OR ((owner_issuer IS NULL) <> (owner_subject IS NULL))
                       OR (owner_issuer IS NOT NULL AND (
                            typeof(owner_issuer) <> 'text'
                            OR typeof(owner_subject) <> 'text'
                            OR length(CAST(owner_issuer AS BLOB)) > 512
                            OR length(CAST(owner_subject AS BLOB)) > 255))
                );

                WITH RECURSIVE
                source(row_key, bytes) AS (
                    SELECT id, CAST(created_by AS BLOB)
                    FROM documents
                    WHERE created_by IS NOT NULL
                ),
                scan(row_key, bytes, byte_count, position, valid) AS (
                    SELECT row_key, bytes, length(bytes), 1, 1 FROM source
                    UNION ALL
                    SELECT row_key, bytes, byte_count,
                        CASE
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F' THEN position + 1
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'C2' AND 'DF' THEN position + 2
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'E0' AND 'EF' THEN position + 3
                            WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'F0' AND 'F4' THEN position + 4
                            ELSE position
                        END,
                        CASE WHEN
                            substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F'
                            OR (substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'C2' AND 'DF'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'E0'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN 'A0' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR ((substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'E1' AND 'EC'
                                    OR substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'EE' AND 'EF')
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'ED'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND '9F'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'F0'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '90' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) BETWEEN 'F1' AND 'F3'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                            OR (substr(hex(bytes), position * 2 - 1, 2) = 'F4'
                                AND substr(hex(bytes), position * 2 + 1, 2) BETWEEN '80' AND '8F'
                                AND substr(hex(bytes), position * 2 + 3, 2) BETWEEN '80' AND 'BF'
                                AND substr(hex(bytes), position * 2 + 5, 2) BETWEEN '80' AND 'BF')
                        THEN 1 ELSE 0 END
                    FROM scan
                    WHERE valid = 1 AND position <= byte_count
                )
                INSERT INTO _structadoc_document_identity_preflight(valid)
                SELECT 0 FROM scan WHERE valid = 0 LIMIT 1;

                WITH RECURSIVE
                owner_source(row_key, bytes) AS (
                    SELECT id || ':issuer', CAST(owner_issuer AS BLOB) FROM documents WHERE owner_issuer IS NOT NULL
                    UNION ALL
                    SELECT id || ':subject', CAST(owner_subject AS BLOB) FROM documents WHERE owner_subject IS NOT NULL
                ),
                owner_scan(row_key, bytes, byte_count, position, valid) AS (
                    SELECT row_key, bytes, length(bytes), 1, 1 FROM owner_source
                    UNION ALL
                    SELECT row_key, bytes, byte_count, position + 1,
                        CASE WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN '00' AND '7F'
                            THEN 1 ELSE 0 END
                    FROM owner_scan
                    WHERE valid = 1 AND position <= byte_count
                )
                INSERT INTO _structadoc_document_identity_preflight(valid)
                SELECT 0 FROM owner_scan WHERE valid = 0 LIMIT 1;

                DROP TABLE _structadoc_document_identity_preflight;
                """);

            migrationBuilder.Sql("""
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE _documents_canonical_identity_new (
                    id TEXT NOT NULL CONSTRAINT PK_documents PRIMARY KEY,
                    created_at_utc TEXT NOT NULL,
                    created_by_issuer BLOB NULL,
                    created_by_subject BLOB NULL,
                    created_by_legacy BLOB NULL,
                    deletion_requested_at_utc TEXT NULL,
                    extension TEXT NOT NULL,
                    lifecycle_state TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    original_file_name TEXT NOT NULL,
                    owner_issuer BLOB NULL,
                    owner_subject BLOB NULL,
                    sha256 TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    storage_ref TEXT NOT NULL,
                    concurrency_version INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT ck_documents_created_by_state CHECK (
                        (created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL)
                        OR (created_by_issuer IS NULL AND created_by_subject IS NULL)),
                    CONSTRAINT ck_documents_owner_state CHECK (
                        (owner_issuer IS NULL AND owner_subject IS NULL)
                        OR (owner_issuer IS NOT NULL AND owner_subject IS NOT NULL))
                );
                INSERT INTO _documents_canonical_identity_new (
                    id, created_at_utc, created_by_issuer, created_by_subject, created_by_legacy,
                    deletion_requested_at_utc, extension, lifecycle_state, media_type,
                    original_file_name, owner_issuer, owner_subject, sha256, size_bytes,
                    storage_ref, concurrency_version)
                SELECT id, created_at_utc, NULL, NULL, CAST(created_by AS BLOB),
                    deletion_requested_at_utc, extension, lifecycle_state, media_type,
                    original_file_name, CAST(owner_issuer AS BLOB), CAST(owner_subject AS BLOB),
                    sha256, size_bytes, storage_ref, concurrency_version
                FROM documents;
                DROP INDEX ix_documents_owner_created_at;
                DROP TABLE documents;
                ALTER TABLE _documents_canonical_identity_new RENAME TO documents;
                CREATE INDEX ix_documents_created_at_id ON documents (created_at_utc, id);
                CREATE INDEX ix_documents_owner_created_at ON documents (owner_issuer, owner_subject, created_at_utc);
                CREATE INDEX ix_documents_sha256 ON documents (sha256);
                COMMIT;
                PRAGMA foreign_keys = ON;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _structadoc_document_identity_down_preflight (
                    valid INTEGER NOT NULL,
                    CONSTRAINT ck_structadoc_document_identity_downgrade CHECK (valid = 1)
                );
                INSERT INTO _structadoc_document_identity_down_preflight(valid)
                SELECT 0 WHERE EXISTS (
                    SELECT 1 FROM documents
                    WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
                );
                DROP TABLE _structadoc_document_identity_down_preflight;
                """);

            migrationBuilder.Sql("""
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE _documents_legacy_identity_new (
                    id TEXT NOT NULL CONSTRAINT PK_documents PRIMARY KEY,
                    created_at_utc TEXT NOT NULL,
                    created_by TEXT NULL,
                    deletion_requested_at_utc TEXT NULL,
                    extension TEXT NOT NULL,
                    lifecycle_state TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    original_file_name TEXT NOT NULL,
                    owner_issuer TEXT NULL,
                    owner_subject TEXT NULL,
                    sha256 TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    storage_ref TEXT NOT NULL,
                    concurrency_version INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO _documents_legacy_identity_new (
                    id, created_at_utc, created_by, deletion_requested_at_utc, extension,
                    lifecycle_state, media_type, original_file_name, owner_issuer, owner_subject,
                    sha256, size_bytes, storage_ref, concurrency_version)
                SELECT id, created_at_utc, CAST(created_by_legacy AS TEXT),
                    deletion_requested_at_utc, extension, lifecycle_state, media_type,
                    original_file_name, CAST(owner_issuer AS TEXT), CAST(owner_subject AS TEXT),
                    sha256, size_bytes, storage_ref, concurrency_version
                FROM documents;
                DROP INDEX ix_documents_owner_created_at;
                DROP TABLE documents;
                ALTER TABLE _documents_legacy_identity_new RENAME TO documents;
                CREATE INDEX ix_documents_created_at_id ON documents (created_at_utc, id);
                CREATE INDEX ix_documents_owner_created_at ON documents (owner_issuer, owner_subject, created_at_utc);
                CREATE INDEX ix_documents_sha256 ON documents (sha256);
                COMMIT;
                PRAGMA foreign_keys = ON;
                """, suppressTransaction: true);
        }
    }
}
