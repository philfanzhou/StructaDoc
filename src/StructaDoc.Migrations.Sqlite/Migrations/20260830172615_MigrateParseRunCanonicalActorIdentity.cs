using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class MigrateParseRunCanonicalActorIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE _structadoc_parse_run_identity_preflight (
                valid INTEGER NOT NULL,
                CONSTRAINT ck_structadoc_parse_runs_require_valid_source CHECK (valid = 1)
            );
            INSERT INTO _structadoc_parse_run_identity_preflight(valid)
            SELECT CASE WHEN encoding = 'UTF-8' THEN 1 ELSE 0 END FROM pragma_encoding;
            INSERT INTO _structadoc_parse_run_identity_preflight(valid)
            SELECT 0 WHERE EXISTS (
                SELECT 1 FROM parse_runs
                WHERE (created_by IS NOT NULL AND (
                        typeof(created_by) <> 'text'
                        OR length(CAST(created_by AS BLOB)) > 1024))
                   OR (idempotency_key IS NOT NULL AND (
                        typeof(idempotency_key) <> 'text'
                        OR length(CAST(idempotency_key AS BLOB)) NOT BETWEEN 1 AND 256))
            );

            WITH RECURSIVE source(row_key, bytes) AS (
                SELECT id, CAST(created_by AS BLOB) FROM parse_runs WHERE created_by IS NOT NULL
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
                FROM scan WHERE valid = 1 AND position <= byte_count
            )
            INSERT INTO _structadoc_parse_run_identity_preflight(valid)
            SELECT 0 FROM scan WHERE valid = 0 LIMIT 1;

            WITH RECURSIVE key_source(row_key, bytes) AS (
                SELECT id, CAST(idempotency_key AS BLOB) FROM parse_runs WHERE idempotency_key IS NOT NULL
            ), key_scan(row_key, bytes, byte_count, position, valid) AS (
                SELECT row_key, bytes, length(bytes), 1, 1 FROM key_source
                UNION ALL
                SELECT row_key, bytes, byte_count, position + 1,
                    CASE WHEN substr(hex(bytes), position * 2 - 1, 2) BETWEEN '21' AND '7E' THEN 1 ELSE 0 END
                FROM key_scan WHERE valid = 1 AND position <= byte_count
            )
            INSERT INTO _structadoc_parse_run_identity_preflight(valid)
            SELECT 0 FROM key_scan WHERE valid = 0 LIMIT 1;
            DROP TABLE _structadoc_parse_run_identity_preflight;
            """);

        migrationBuilder.Sql("""
            PRAGMA foreign_keys = OFF;
            BEGIN IMMEDIATE;
            CREATE TABLE _parse_runs_canonical_identity_new (
                id TEXT NOT NULL CONSTRAINT PK_parse_runs PRIMARY KEY,
                document_id TEXT NOT NULL,
                status TEXT NOT NULL,
                stage TEXT NULL,
                provider_type TEXT NOT NULL,
                provider_config_id TEXT NOT NULL,
                provider_config_version TEXT NOT NULL,
                options_json TEXT NOT NULL,
                source_media_type TEXT NOT NULL,
                submitted_media_type TEXT NOT NULL,
                conversion_json TEXT NULL,
                external_task_id TEXT NULL,
                protected_submission_continuation TEXT NULL,
                result_schema_version TEXT NULL,
                result_sha256 TEXT NULL,
                provider_metadata_json TEXT NULL,
                attempt_count INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                next_attempt_at_utc TEXT NOT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                created_by_issuer BLOB NULL,
                created_by_subject BLOB NULL,
                created_by_legacy BLOB NULL,
                idempotency_key TEXT COLLATE BINARY NULL,
                claimed_by TEXT NULL,
                lease_expires_at_utc TEXT NULL,
                created_at_utc TEXT NOT NULL,
                started_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                concurrency_version INTEGER NOT NULL,
                lifecycle_state TEXT NOT NULL DEFAULT 'active',
                deletion_requested_at_utc TEXT NULL,
                CONSTRAINT ck_parse_runs_created_by_state CHECK (
                    (created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL)
                    OR (created_by_issuer IS NULL AND created_by_subject IS NULL)),
                CONSTRAINT FK_parse_runs_documents_document_id FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE RESTRICT
            );
            INSERT INTO _parse_runs_canonical_identity_new (
                id, document_id, status, stage, provider_type, provider_config_id,
                provider_config_version, options_json, source_media_type, submitted_media_type,
                conversion_json, external_task_id, protected_submission_continuation,
                result_schema_version, result_sha256, provider_metadata_json, attempt_count,
                max_attempts, next_attempt_at_utc, error_code, error_message, created_by_issuer,
                created_by_subject, created_by_legacy, idempotency_key, claimed_by,
                lease_expires_at_utc, created_at_utc, started_at_utc, completed_at_utc,
                concurrency_version, lifecycle_state, deletion_requested_at_utc)
            SELECT id, document_id, status, stage, provider_type, provider_config_id,
                provider_config_version, options_json, source_media_type, submitted_media_type,
                conversion_json, external_task_id, protected_submission_continuation,
                result_schema_version, result_sha256, provider_metadata_json, attempt_count,
                max_attempts, next_attempt_at_utc, error_code, error_message, NULL, NULL,
                CAST(created_by AS BLOB), idempotency_key, claimed_by, lease_expires_at_utc,
                created_at_utc, started_at_utc, completed_at_utc, concurrency_version,
                lifecycle_state, deletion_requested_at_utc
            FROM parse_runs;
            DROP INDEX ux_parse_runs_idempotency;
            DROP TABLE parse_runs;
            ALTER TABLE _parse_runs_canonical_identity_new RENAME TO parse_runs;
            CREATE INDEX ix_parse_runs_document_id ON parse_runs (document_id);
            CREATE INDEX ix_parse_runs_due ON parse_runs (status, next_attempt_at_utc);
            CREATE INDEX ix_parse_runs_lease_expiry ON parse_runs (lease_expires_at_utc);
            CREATE INDEX ix_parse_runs_legacy_idempotency ON parse_runs (document_id, idempotency_key);
            CREATE UNIQUE INDEX ux_parse_runs_idempotency ON parse_runs (created_by_issuer, created_by_subject, document_id, idempotency_key);
            COMMIT;
            PRAGMA foreign_keys = ON;
            """, suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE _structadoc_parse_run_identity_down (
                valid INTEGER NOT NULL,
                CONSTRAINT ck_structadoc_parse_run_identity_downgrade CHECK (valid = 1)
            );
            INSERT INTO _structadoc_parse_run_identity_down(valid)
            SELECT 0 WHERE EXISTS (
                SELECT 1 FROM parse_runs
                WHERE created_by_issuer IS NOT NULL OR created_by_subject IS NOT NULL
            );
            DROP TABLE _structadoc_parse_run_identity_down;
            """);

        migrationBuilder.Sql("""
            PRAGMA foreign_keys = OFF;
            BEGIN IMMEDIATE;
            CREATE TABLE _parse_runs_legacy_identity_new (
                id TEXT NOT NULL CONSTRAINT PK_parse_runs PRIMARY KEY,
                document_id TEXT NOT NULL,
                status TEXT NOT NULL,
                stage TEXT NULL,
                provider_type TEXT NOT NULL,
                provider_config_id TEXT NOT NULL,
                provider_config_version TEXT NOT NULL,
                options_json TEXT NOT NULL,
                source_media_type TEXT NOT NULL,
                submitted_media_type TEXT NOT NULL,
                conversion_json TEXT NULL,
                external_task_id TEXT NULL,
                protected_submission_continuation TEXT NULL,
                result_schema_version TEXT NULL,
                result_sha256 TEXT NULL,
                provider_metadata_json TEXT NULL,
                attempt_count INTEGER NOT NULL,
                max_attempts INTEGER NOT NULL,
                next_attempt_at_utc TEXT NOT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                created_by TEXT NULL,
                idempotency_key TEXT NULL,
                claimed_by TEXT NULL,
                lease_expires_at_utc TEXT NULL,
                created_at_utc TEXT NOT NULL,
                started_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                concurrency_version INTEGER NOT NULL,
                lifecycle_state TEXT NOT NULL DEFAULT 'active',
                deletion_requested_at_utc TEXT NULL,
                CONSTRAINT FK_parse_runs_documents_document_id FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE RESTRICT
            );
            INSERT INTO _parse_runs_legacy_identity_new (
                id, document_id, status, stage, provider_type, provider_config_id,
                provider_config_version, options_json, source_media_type, submitted_media_type,
                conversion_json, external_task_id, protected_submission_continuation,
                result_schema_version, result_sha256, provider_metadata_json, attempt_count,
                max_attempts, next_attempt_at_utc, error_code, error_message, created_by,
                idempotency_key, claimed_by, lease_expires_at_utc, created_at_utc,
                started_at_utc, completed_at_utc, concurrency_version, lifecycle_state,
                deletion_requested_at_utc)
            SELECT id, document_id, status, stage, provider_type, provider_config_id,
                provider_config_version, options_json, source_media_type, submitted_media_type,
                conversion_json, external_task_id, protected_submission_continuation,
                result_schema_version, result_sha256, provider_metadata_json, attempt_count,
                max_attempts, next_attempt_at_utc, error_code, error_message,
                CAST(created_by_legacy AS TEXT), idempotency_key, claimed_by,
                lease_expires_at_utc, created_at_utc, started_at_utc, completed_at_utc,
                concurrency_version, lifecycle_state, deletion_requested_at_utc
            FROM parse_runs;
            DROP INDEX ux_parse_runs_idempotency;
            DROP INDEX ix_parse_runs_legacy_idempotency;
            DROP TABLE parse_runs;
            ALTER TABLE _parse_runs_legacy_identity_new RENAME TO parse_runs;
            CREATE INDEX ix_parse_runs_document_id ON parse_runs (document_id);
            CREATE INDEX ix_parse_runs_due ON parse_runs (status, next_attempt_at_utc);
            CREATE INDEX ix_parse_runs_lease_expiry ON parse_runs (lease_expires_at_utc);
            CREATE UNIQUE INDEX ux_parse_runs_idempotency ON parse_runs (created_by, document_id, idempotency_key);
            COMMIT;
            PRAGMA foreign_keys = ON;
            """, suppressTransaction: true);
    }
}
