using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.Documents;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;

namespace StructaDoc.DatabaseContractTests;

internal static class DocumentIdentityMigrationContract
{
    private static readonly Guid LegacyDocumentId = Guid.Parse(
        "11111111-1111-1111-1111-111111111111");

    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(provider, connectionString, serverVersion);
        var previousMigration = provider switch
        {
            DatabaseProvider.Sqlite => "20260821021418_AddDocumentConcurrencyGate",
            DatabaseProvider.PostgreSql => "20260821021422_AddDocumentConcurrencyGate",
            DatabaseProvider.MySql => "20260821021427_AddDocumentConcurrencyGate",
            DatabaseProvider.MariaDb => "20260821021431_AddDocumentConcurrencyGate",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);
        await SeedLegacyDocumentAsync(options, cancellationToken);

        await using (var context = new StructaDocDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        await AssertLegacyBackfillAsync(options, cancellationToken);
        await AssertRuntimeCutoverAndAuthorizationAsync(options, cancellationToken);
        await AssertStateConstraintsAsync(options, cancellationToken);
        await AssertPhysicalContractAsync(provider, options, cancellationToken);
        await AssertAccessGrantUpgradeAsync(provider, options, cancellationToken);
        await AssertParseRunUpgradeAsync(provider, options, cancellationToken);

        // The following Parse Run contract shares this database and expects to seed an empty,
        // current schema. Leave that deterministic handoff regardless of the provider.
        await ResetAndMigrateAsync(options, targetMigration: null, cancellationToken);
    }

    private static async Task AssertAccessGrantUpgradeAsync(
        DatabaseProvider provider,
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        var previousMigration = provider switch
        {
            DatabaseProvider.Sqlite => "20260830160438_MigrateDocumentCanonicalActorIdentity",
            DatabaseProvider.PostgreSql => "20260830160429_MigrateDocumentCanonicalActorIdentity",
            DatabaseProvider.MySql => "20260830160429_MigrateDocumentCanonicalActorIdentity",
            DatabaseProvider.MariaDb => "20260830160429_MigrateDocumentCanonicalActorIdentity",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);
        var documentId = Guid.NewGuid();
        await using (var context = new StructaDocDbContext(options))
        {
            context.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "access-grant-upgrade.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('3', 64),
                StorageRef = "documents/access-grant-upgrade/original",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        var legacyActor = string.Concat(Enumerable.Repeat("😀", 1024));
        var legacyGrantId = Guid.NewGuid();
        await ExecuteAsync(
            options,
            """
            INSERT INTO document_access_grants (
                id, document_id, principal_issuer, principal_subject, permissions,
                created_by, created_at_utc)
            VALUES (@id, @documentId, @issuer, @subject, @permissions, @createdBy, @createdAt)
            """,
            cancellationToken,
            ("@id", legacyGrantId),
            ("@documentId", documentId),
            ("@issuer", "https://identity-a.example"),
            ("@subject", "same-subject"),
            ("@permissions", (int)DocumentPermissions.Read),
            ("@createdBy", legacyActor),
            ("@createdAt", DateTime.UtcNow));

        await using (var context = new StructaDocDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        var runtimeActor = CanonicalActor.Create(
            CanonicalActor.AdministratorIssuer,
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        const string boundaryIssuerPrefix = "https://identity-b.example/";
        var boundaryIssuer = boundaryIssuerPrefix + new string(
            'i',
            CanonicalActorPersistence.MaximumIssuerByteCount - boundaryIssuerPrefix.Length);
        var boundarySubject = "subject\0" + new string(
            's',
            CanonicalActorPersistence.MaximumSubjectByteCount - 8);

        await using (var context = new StructaDocDbContext(options))
        {
            var service = new EfCoreDocumentAuthorizationService(context);
            Assert.NotNull(await service.SetGrantAsync(
                documentId,
                ResourceAccessContext.System,
                "https://identity-b.example",
                "same-subject",
                DocumentPermissions.Read,
                runtimeActor,
                DateTime.UtcNow,
                cancellationToken));
            var boundary = Assert.IsType<DocumentAccessGrant>(await service.SetGrantAsync(
                documentId,
                ResourceAccessContext.System,
                boundaryIssuer,
                boundarySubject,
                DocumentPermissions.Read,
                runtimeActor,
                DateTime.UtcNow,
                cancellationToken));
            Assert.Equal(boundaryIssuer, boundary.Issuer);
            Assert.Equal(boundarySubject, boundary.Subject);
            Assert.Equal("administrator:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", boundary.CreatedBy);

            var grants = await service.ListGrantsAsync(
                documentId,
                ResourceAccessContext.System,
                cancellationToken);
            Assert.Contains(grants, grant =>
                grant.Id == legacyGrantId
                && grant.CreatedBy == legacyActor
                && grant.Issuer == "https://identity-a.example"
                && grant.Subject == "same-subject");
            Assert.True(await service.HasPermissionAsync(
                documentId,
                new ResourceAccessContext(false, "https://identity-a.example", "same-subject"),
                DocumentPermissions.Read,
                cancellationToken));
            Assert.True(await service.HasPermissionAsync(
                documentId,
                new ResourceAccessContext(false, "https://identity-b.example", "same-subject"),
                DocumentPermissions.Read,
                cancellationToken));
            Assert.True(await service.HasPermissionAsync(
                documentId,
                new ResourceAccessContext(false, boundaryIssuer, boundarySubject),
                DocumentPermissions.Read,
                cancellationToken));
            Assert.False(await service.HasPermissionAsync(
                documentId,
                new ResourceAccessContext(false, "https://identity-c.example", "same-subject"),
                DocumentPermissions.Read,
                cancellationToken));

            var legacy = await context.DocumentAccessGrants.AsNoTracking()
                .SingleAsync(grant => grant.Id == legacyGrantId, cancellationToken);
            Assert.Equal(Encoding.UTF8.GetBytes(legacyActor), legacy.CreatedByLegacy);
            Assert.Null(legacy.CreatedByIssuer);
            Assert.Null(legacy.CreatedBySubject);
            Assert.Equal(
                CanonicalActorPersistence.EncodeIssuer("https://identity-a.example"),
                legacy.PrincipalIssuer);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            options,
            "UPDATE document_access_grants SET created_by_legacy = @legacy WHERE created_by_issuer IS NOT NULL",
            cancellationToken,
            ("@legacy", new byte[] { 1 })));

        var typeCountSql = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT COUNT(*) FROM pragma_table_info('document_access_grants') WHERE name IN ('created_by_issuer','created_by_subject','created_by_legacy','principal_issuer','principal_subject') AND upper(type) = 'BLOB'",
            DatabaseProvider.PostgreSql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'document_access_grants' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy','principal_issuer','principal_subject') AND data_type = 'bytea'",
            _ => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'document_access_grants' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy','principal_issuer','principal_subject') AND data_type = 'varbinary'",
        };
        Assert.Equal(5L, await ScalarInt64Async(options, typeCountSql, cancellationToken));

        var indexSql = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT group_concat(name, ',') FROM pragma_index_info('ux_document_access_grants_principal') ORDER BY seqno",
            DatabaseProvider.PostgreSql => "SELECT indexdef FROM pg_indexes WHERE tablename = 'document_access_grants' AND indexname = 'ux_document_access_grants_principal'",
            _ => "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'document_access_grants' AND index_name = 'ux_document_access_grants_principal' AND non_unique = 0",
        };
        var index = await ScalarStringAsync(options, indexSql, cancellationToken);
        if (provider == DatabaseProvider.PostgreSql)
        {
            Assert.Contains(
                "(document_id, principal_issuer, principal_subject)",
                index,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(
                "document_id,principal_issuer,principal_subject",
                index,
                ignoreCase: true);
        }
        if (provider is DatabaseProvider.MySql or DatabaseProvider.MariaDb)
        {
            Assert.Equal(
                "Dynamic",
                await ScalarStringAsync(
                    options,
                    "SELECT row_format FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'document_access_grants'",
                    cancellationToken),
                ignoreCase: true);
        }
    }

    private static async Task AssertParseRunUpgradeAsync(
        DatabaseProvider provider,
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        var previousMigration = provider switch
        {
            DatabaseProvider.Sqlite => "20260830165343_MigrateAccessGrantCanonicalActorIdentity",
            DatabaseProvider.PostgreSql => "20260830165343_MigrateAccessGrantCanonicalActorIdentity",
            DatabaseProvider.MySql => "20260830165343_MigrateAccessGrantCanonicalActorIdentity",
            DatabaseProvider.MariaDb => "20260830165343_MigrateAccessGrantCanonicalActorIdentity",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);

        await using (var context = new StructaDocDbContext(options))
        {
            var script = context.Database.GetService<IMigrator>().GenerateScript(
                previousMigration,
                "20260830172615_MigrateParseRunCanonicalActorIdentity");
            var dropIndexSql = provider is DatabaseProvider.MySql or DatabaseProvider.MariaDb
                ? "DROP INDEX `ux_parse_runs_idempotency`"
                : "DROP INDEX ux_parse_runs_idempotency";
            var createIndexSql = provider is DatabaseProvider.MySql or DatabaseProvider.MariaDb
                ? "CREATE UNIQUE INDEX `ux_parse_runs_idempotency`"
                : "CREATE UNIQUE INDEX ux_parse_runs_idempotency";
            Assert.Equal(1, CountOccurrences(script, dropIndexSql));
            Assert.Equal(1, CountOccurrences(script, createIndexSql));
        }

        var documentId = Guid.NewGuid();
        var providerConfigId = Guid.NewGuid();
        var providerVersionId = Guid.NewGuid();
        var legacyRunId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        await using (var context = new StructaDocDbContext(options))
        {
            context.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "parse-run-upgrade.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('5', 64),
                StorageRef = "documents/parse-run-upgrade/original",
                CreatedAtUtc = nowUtc,
            });
            var config = new ProviderConfigEntity
            {
                Id = providerConfigId,
                Name = "Parse Run identity contract",
                ProviderType = "mineru-local",
                IsEnabled = true,
                CurrentVersionId = providerVersionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            context.ProviderConfigs.Add(config);
            context.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = providerVersionId,
                ProviderConfigId = providerConfigId,
                ProviderConfig = config,
                VersionNumber = 1,
                BaseUrl = "http://provider.test/",
                CreatedAtUtc = nowUtc,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        const string legacyActor = "oidc:https://identity.example|CaseSubject";
        const string legacyKey = "ReplayKey";
        await ExecuteAsync(
            options,
            """
            INSERT INTO parse_runs (
                id, document_id, status, provider_type, provider_config_id,
                provider_config_version, options_json, source_media_type,
                submitted_media_type, attempt_count, max_attempts, next_attempt_at_utc,
                created_by, idempotency_key, created_at_utc, concurrency_version,
                lifecycle_state)
            VALUES (
                @id, @documentId, 'queued', 'mineru-local', @providerConfigId,
                @providerVersionId, '{}', 'application/pdf', 'application/pdf',
                0, 3, @createdAt, @createdBy, @idempotencyKey, @createdAt, 0, 'active')
            """,
            cancellationToken,
            ("@id", legacyRunId),
            ("@documentId", documentId),
            ("@providerConfigId", providerConfigId),
            ("@providerVersionId", providerVersionId),
            ("@createdAt", nowUtc),
            ("@createdBy", legacyActor),
            ("@idempotencyKey", legacyKey));

        await using (var context = new StructaDocDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        await using (var context = new StructaDocDbContext(options))
        {
            var legacy = await context.ParseRuns.AsNoTracking()
                .SingleAsync(run => run.Id == legacyRunId, cancellationToken);
            Assert.Null(legacy.CreatedByIssuer);
            Assert.Null(legacy.CreatedBySubject);
            Assert.Equal(Encoding.UTF8.GetBytes(legacyActor), legacy.CreatedByLegacy);

            var service = new EfCoreParseRunService(context);
            var exactActor = CanonicalActor.Create("https://identity.example", "CaseSubject");
            var exact = await service.CreateAsync(
                Request(exactActor, legacyKey),
                cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Replayed, exact.Status);
            Assert.Equal(legacyRunId, exact.ParseRun!.Id);

            var actorAlias = await service.CreateAsync(
                Request(CanonicalActor.Create("https://identity.example", "casesubject"), legacyKey),
                cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Created, actorAlias.Status);
            Assert.NotEqual(legacyRunId, actorAlias.ParseRun!.Id);

            var keyAlias = await service.CreateAsync(
                Request(exactActor, "replaykey"),
                cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Created, keyAlias.Status);
            Assert.NotEqual(legacyRunId, keyAlias.ParseRun!.Id);

            var otherIssuer = await service.CreateAsync(
                Request(CanonicalActor.Create("https://identity-alt.example", "CaseSubject"), legacyKey),
                cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Created, otherIssuer.Status);
        }

        const string boundaryIssuerPrefix = "https://identity-boundary.example/";
        var boundaryActor = CanonicalActor.Create(
            boundaryIssuerPrefix + new string(
                'i',
                CanonicalActorPersistence.MaximumIssuerByteCount - boundaryIssuerPrefix.Length),
            "subject\0" + new string(
                's',
                CanonicalActorPersistence.MaximumSubjectByteCount - 8));
        var boundaryKey = new string('K', 256);
        Guid boundaryRunId;
        await using (var context = new StructaDocDbContext(options))
        {
            var service = new EfCoreParseRunService(context);
            var created = await service.CreateAsync(Request(boundaryActor, boundaryKey), cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Created, created.Status);
            boundaryRunId = created.ParseRun!.Id;
            var replay = await service.CreateAsync(Request(boundaryActor, boundaryKey), cancellationToken);
            Assert.Equal(ParseRunCreationStatus.Replayed, replay.Status);
            Assert.Equal(boundaryRunId, replay.ParseRun!.Id);
        }

        await using (var context = new StructaDocDbContext(options))
        {
            var boundary = await context.ParseRuns.AsNoTracking()
                .SingleAsync(run => run.Id == boundaryRunId, cancellationToken);
            Assert.Equal(boundaryActor.EncodeIssuer(), boundary.CreatedByIssuer);
            Assert.Equal(boundaryActor.EncodeSubject(), boundary.CreatedBySubject);
            Assert.Null(boundary.CreatedByLegacy);
        }

        var concurrentActor = CanonicalActor.Create(
            CanonicalActor.AdministratorIssuer,
            "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var concurrentResults = await Task.WhenAll(
            CreateInOwnContextAsync("ConcurrentKey"),
            CreateInOwnContextAsync("ConcurrentKey"));
        Assert.Single(concurrentResults, result => result.Status == ParseRunCreationStatus.Created);
        Assert.Single(concurrentResults, result => result.Status == ParseRunCreationStatus.Replayed);
        Assert.Single(concurrentResults.Select(result => result.ParseRun!.Id).Distinct());

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            options,
            "UPDATE parse_runs SET created_by_legacy = @legacy WHERE created_by_issuer IS NOT NULL",
            cancellationToken,
            ("@legacy", new byte[] { 1 })));

        var binaryTypeSql = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT COUNT(*) FROM pragma_table_info('parse_runs') WHERE name IN ('created_by_issuer','created_by_subject','created_by_legacy') AND upper(type) = 'BLOB'",
            DatabaseProvider.PostgreSql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'parse_runs' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy') AND data_type = 'bytea'",
            _ => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'parse_runs' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy') AND data_type = 'varbinary'",
        };
        Assert.Equal(3L, await ScalarInt64Async(options, binaryTypeSql, cancellationToken));

        var uniqueIndexSql = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT group_concat(name, ',') FROM pragma_index_info('ux_parse_runs_idempotency') ORDER BY seqno",
            DatabaseProvider.PostgreSql => "SELECT indexdef FROM pg_indexes WHERE tablename = 'parse_runs' AND indexname = 'ux_parse_runs_idempotency'",
            _ => "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'parse_runs' AND index_name = 'ux_parse_runs_idempotency' AND non_unique = 0",
        };
        var uniqueIndex = await ScalarStringAsync(options, uniqueIndexSql, cancellationToken);
        if (provider == DatabaseProvider.PostgreSql)
        {
            Assert.Contains(
                "(created_by_issuer, created_by_subject, document_id, idempotency_key)",
                uniqueIndex,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(
                "created_by_issuer,created_by_subject,document_id,idempotency_key",
                uniqueIndex,
                ignoreCase: true);
        }

        var helperIndexSql = provider switch
        {
            DatabaseProvider.Sqlite => "SELECT group_concat(name, ',') FROM pragma_index_info('ix_parse_runs_legacy_idempotency') ORDER BY seqno",
            DatabaseProvider.PostgreSql => "SELECT indexdef FROM pg_indexes WHERE tablename = 'parse_runs' AND indexname = 'ix_parse_runs_legacy_idempotency'",
            _ => "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'parse_runs' AND index_name = 'ix_parse_runs_legacy_idempotency' AND non_unique = 1",
        };
        var helperIndex = await ScalarStringAsync(options, helperIndexSql, cancellationToken);
        Assert.Contains("document_id", helperIndex, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency_key", helperIndex, StringComparison.OrdinalIgnoreCase);

        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                Assert.Contains(
                    "idempotency_key TEXT COLLATE BINARY",
                    await ScalarStringAsync(
                        options,
                        "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'parse_runs'",
                        cancellationToken),
                    StringComparison.OrdinalIgnoreCase);
                break;
            case DatabaseProvider.PostgreSql:
                Assert.Equal(
                    "C",
                    await ScalarStringAsync(
                        options,
                        "SELECT collation_name FROM information_schema.columns WHERE table_name = 'parse_runs' AND column_name = 'idempotency_key'",
                        cancellationToken));
                break;
            case DatabaseProvider.MySql:
            case DatabaseProvider.MariaDb:
                Assert.Equal(
                    "ascii:ascii_bin",
                    await ScalarStringAsync(
                        options,
                        "SELECT CONCAT(character_set_name, ':', collation_name) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'parse_runs' AND column_name = 'idempotency_key'",
                        cancellationToken),
                    ignoreCase: true);
                Assert.Equal(
                    "Dynamic",
                    await ScalarStringAsync(
                        options,
                        "SELECT row_format FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'parse_runs'",
                        cancellationToken),
                    ignoreCase: true);
                break;
        }

        ParseRunCreateRequest Request(CanonicalActor actor, string key) => new(
            documentId,
            providerConfigId,
            "{}",
            3,
            actor,
            key,
            DateTime.UtcNow);

        async Task<ParseRunCreationResult> CreateInOwnContextAsync(string key)
        {
            await using var context = new StructaDocDbContext(options);
            return await new EfCoreParseRunService(context).CreateAsync(
                Request(concurrentActor, key),
                cancellationToken);
        }
    }

    public static async Task AssertSqlitePreflightAsync(string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(DatabaseProvider.Sqlite, connectionString, serverVersion: null);
        const string previousMigration = "20260821021418_AddDocumentConcurrencyGate";
        const string targetMigration = "20260830160438_MigrateDocumentCanonicalActorIdentity";

        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);
        await using (var context = new StructaDocDbContext(options))
        {
            var script = context.Database.GetService<IMigrator>().GenerateScript(
                previousMigration,
                targetMigration);
            Assert.Equal(1, CountOccurrences(script, "CREATE TABLE _documents_canonical_identity_new"));
            Assert.DoesNotContain("ef_temp_documents", script, StringComparison.OrdinalIgnoreCase);
        }

        await ExecuteAsync(
            options,
            """
            INSERT INTO documents (
                id, created_at_utc, created_by, extension, lifecycle_state, media_type,
                original_file_name, sha256, size_bytes, storage_ref, concurrency_version)
            VALUES (
                @id, @createdAt, CAST(x'61C32862' AS TEXT), '.pdf', 'active',
                'application/pdf', 'invalid.pdf', @sha256, 1, 'documents/invalid/original', 0)
            """,
            cancellationToken,
            ("@id", Guid.NewGuid()),
            ("@createdAt", DateTime.UtcNow),
            ("@sha256", new string('0', 64)));

        await using (var context = new StructaDocDbContext(options))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.MigrateAsync(cancellationToken));
            Assert.Contains(
                "ck_structadoc_documents_require_utf8",
                error.ToString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name = 'created_by'",
                cancellationToken));
        Assert.Equal(
            0L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name = 'created_by_legacy'",
                cancellationToken));

        await using (var reset = new StructaDocDbContext(options))
        {
            await reset.Database.EnsureDeletedAsync(cancellationToken);
        }

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA encoding = 'UTF-16le';
                CREATE TABLE encoding_seed (value INTEGER NOT NULL);
                DROP TABLE encoding_seed;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var context = new StructaDocDbContext(options))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(previousMigration, cancellationToken);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.MigrateAsync(cancellationToken));
            Assert.Contains(
                "ck_structadoc_documents_require_utf8",
                error.ToString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name = 'created_by'",
                cancellationToken));
    }

    public static async Task AssertSqliteAccessGrantPreflightAsync(string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(DatabaseProvider.Sqlite, connectionString, serverVersion: null);
        const string previousMigration = "20260830160438_MigrateDocumentCanonicalActorIdentity";
        const string targetMigration = "20260830165343_MigrateAccessGrantCanonicalActorIdentity";
        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);

        await using (var context = new StructaDocDbContext(options))
        {
            var script = context.Database.GetService<IMigrator>().GenerateScript(
                previousMigration,
                targetMigration);
            Assert.Equal(
                1,
                CountOccurrences(
                    script,
                    "CREATE TABLE _document_access_grants_canonical_new"));
            var documentId = Guid.NewGuid();
            context.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "invalid-grant.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('4', 64),
                StorageRef = "documents/invalid-grant/original",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync(cancellationToken);
            const string issuer = "https://identity.example";
            const string subject = "subject";
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO document_access_grants (
                    id, document_id, principal_issuer, principal_subject, permissions,
                    created_by, created_at_utc)
                VALUES (
                    {Guid.NewGuid()}, {documentId}, {issuer}, {subject},
                    {(int)DocumentPermissions.Read}, CAST(x'61C32862' AS TEXT), {DateTime.UtcNow})
                """, cancellationToken);
        }

        await using (var context = new StructaDocDbContext(options))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.MigrateAsync(cancellationToken));
            Assert.Contains(
                "ck_structadoc_access_grants_require_utf8",
                error.ToString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('document_access_grants') WHERE name = 'created_by'",
                cancellationToken));
        Assert.Equal(
            0L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('document_access_grants') WHERE name = 'created_by_legacy'",
                cancellationToken));
    }

    public static async Task AssertSqliteParseRunPreflightAsync(string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(DatabaseProvider.Sqlite, connectionString, serverVersion: null);
        const string previousMigration = "20260830165343_MigrateAccessGrantCanonicalActorIdentity";
        const string targetMigration = "20260830172615_MigrateParseRunCanonicalActorIdentity";
        await ResetAndMigrateAsync(options, previousMigration, cancellationToken);

        Guid documentId;
        await using (var context = new StructaDocDbContext(options))
        {
            var script = context.Database.GetService<IMigrator>().GenerateScript(
                previousMigration,
                targetMigration);
            Assert.Equal(
                1,
                CountOccurrences(script, "CREATE TABLE _parse_runs_canonical_identity_new"));
            Assert.Equal(1, CountOccurrences(script, "DROP INDEX ux_parse_runs_idempotency"));
            Assert.Equal(
                1,
                CountOccurrences(script, "CREATE UNIQUE INDEX ux_parse_runs_idempotency"));
            Assert.DoesNotContain("ef_temp_parse_runs", script, StringComparison.OrdinalIgnoreCase);

            documentId = Guid.NewGuid();
            context.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "invalid-parse-run.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('6', 64),
                StorageRef = "documents/invalid-parse-run/original",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await InsertLegacyParseRunAsync("CAST(x'61C32862' AS TEXT)", "'ValidKey'");
        await AssertPreflightFailureAsync();
        await ExecuteAsync(options, "DELETE FROM parse_runs", cancellationToken);
        await InsertLegacyParseRunAsync("'valid-actor'", "'invalid key'");
        await AssertPreflightFailureAsync();

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('parse_runs') WHERE name = 'created_by'",
                cancellationToken));
        Assert.Equal(
            0L,
            await ScalarInt64Async(
                options,
                "SELECT COUNT(*) FROM pragma_table_info('parse_runs') WHERE name = 'created_by_legacy'",
                cancellationToken));

        async Task InsertLegacyParseRunAsync(string actorSql, string keySql) =>
            await ExecuteAsync(
                options,
                $$"""
                INSERT INTO parse_runs (
                    id, document_id, status, provider_type, provider_config_id,
                    provider_config_version, options_json, source_media_type,
                    submitted_media_type, attempt_count, max_attempts, next_attempt_at_utc,
                    created_by, idempotency_key, created_at_utc, concurrency_version,
                    lifecycle_state)
                VALUES (
                    @id, @documentId, 'queued', 'test-provider', @providerConfigId,
                    @providerVersionId, '{}', 'application/pdf', 'application/pdf',
                    0, 3, @createdAt, {{actorSql}}, {{keySql}}, @createdAt, 0, 'active')
                """,
                cancellationToken,
                ("@id", Guid.NewGuid()),
                ("@documentId", documentId),
                ("@providerConfigId", Guid.NewGuid()),
                ("@providerVersionId", Guid.NewGuid()),
                ("@createdAt", DateTime.UtcNow));

        async Task AssertPreflightFailureAsync()
        {
            await using var context = new StructaDocDbContext(options);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.MigrateAsync(cancellationToken));
            Assert.Contains(
                "ck_structadoc_parse_runs_require_valid_source",
                error.ToString(),
                StringComparison.Ordinal);
        }
    }

    private static async Task SeedLegacyDocumentAsync(
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            options,
            """
            INSERT INTO documents (
                id, created_at_utc, created_by, extension, lifecycle_state, media_type,
                original_file_name, owner_issuer, owner_subject, sha256, size_bytes,
                storage_ref, concurrency_version)
            VALUES (
                @id, @createdAt, @createdBy, '.pdf', 'active', 'application/pdf',
                'legacy.pdf', @ownerIssuer, @ownerSubject, @sha256, 1,
                'documents/legacy/original', 0)
            """,
            cancellationToken,
            ("@id", LegacyDocumentId),
            ("@createdAt", DateTime.UtcNow),
            ("@createdBy", "legacy-演员"),
            ("@ownerIssuer", "https://identity-a.example"),
            ("@ownerSubject", "same-subject"),
            ("@sha256", new string('1', 64)));
    }

    private static async Task AssertLegacyBackfillAsync(
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        await using var context = new StructaDocDbContext(options);
        var row = await context.Documents.AsNoTracking()
            .SingleAsync(document => document.Id == LegacyDocumentId, cancellationToken);
        Assert.Null(row.CreatedByIssuer);
        Assert.Null(row.CreatedBySubject);
        Assert.Equal(Encoding.UTF8.GetBytes("legacy-演员"), row.CreatedByLegacy);
        Assert.Equal(
            CanonicalActorPersistence.EncodeIssuer("https://identity-a.example"),
            row.OwnerIssuer);
        Assert.Equal(
            CanonicalActorPersistence.EncodeSubject("same-subject"),
            row.OwnerSubject);
    }

    private static async Task AssertRuntimeCutoverAndAuthorizationAsync(
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        const string issuerPrefix = "https://identity-b.example/";
        var issuer = issuerPrefix + new string(
            'i',
            CanonicalActorPersistence.MaximumIssuerByteCount - issuerPrefix.Length);
        var subject = "same-subject\0" + new string(
            's',
            CanonicalActorPersistence.MaximumSubjectByteCount - 13);
        var actor = CanonicalActor.Create(issuer, subject);
        var storage = new ContractFileStorage();

        Guid runtimeDocumentId;
        await using (var context = new StructaDocDbContext(options))
        {
            var service = new EfCoreDocumentIngestionService(
                context,
                storage,
                new PdfDocumentTypeDetector(),
                new DocumentIngestionOptions { MaxUploadBytes = 1024 },
                TimeProvider.System,
                NullLogger<EfCoreDocumentIngestionService>.Instance);
            await using var content = new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray());
            var document = await service.IngestAsync(
                new DocumentIngestionRequest(
                    "boundary.pdf",
                    "application/pdf",
                    content,
                    actor,
                    actor),
                cancellationToken);
            runtimeDocumentId = document.Id;
        }

        await using (var context = new StructaDocDbContext(options))
        {
            var row = await context.Documents.AsNoTracking()
                .SingleAsync(document => document.Id == runtimeDocumentId, cancellationToken);
            Assert.Equal(actor.EncodeIssuer(), row.CreatedByIssuer);
            Assert.Equal(actor.EncodeSubject(), row.CreatedBySubject);
            Assert.Null(row.CreatedByLegacy);
            Assert.Equal(actor.EncodeIssuer(), row.OwnerIssuer);
            Assert.Equal(actor.EncodeSubject(), row.OwnerSubject);

            var authorization = new EfCoreDocumentAuthorizationService(context);
            Assert.True(await authorization.HasPermissionAsync(
                runtimeDocumentId,
                new ResourceAccessContext(false, issuer, subject),
                DocumentPermissions.Read,
                cancellationToken));
            Assert.False(await authorization.HasPermissionAsync(
                LegacyDocumentId,
                new ResourceAccessContext(false, issuer, "same-subject"),
                DocumentPermissions.Read,
                cancellationToken));
            Assert.True(await authorization.HasPermissionAsync(
                LegacyDocumentId,
                new ResourceAccessContext(
                    false,
                    "https://identity-a.example",
                    "same-subject"),
                DocumentPermissions.Read,
                cancellationToken));
        }
    }

    private static async Task AssertStateConstraintsAsync(
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            options,
            "UPDATE documents SET created_by_legacy = @legacy WHERE created_by_issuer IS NOT NULL",
            cancellationToken,
            ("@legacy", new byte[] { 1 })));
        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(
            options,
            "UPDATE documents SET owner_subject = NULL WHERE owner_issuer IS NOT NULL",
            cancellationToken));
    }

    private static async Task AssertPhysicalContractAsync(
        DatabaseProvider provider,
        DbContextOptions<StructaDocDbContext> options,
        CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                Assert.Equal(
                    5L,
                    await ScalarInt64Async(
                        options,
                        "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name IN ('created_by_issuer','created_by_subject','created_by_legacy','owner_issuer','owner_subject') AND upper(type) = 'BLOB'",
                        cancellationToken));
                Assert.Equal(
                    "owner_issuer,owner_subject,created_at_utc",
                    await ScalarStringAsync(
                        options,
                        "SELECT group_concat(name, ',') FROM pragma_index_info('ix_documents_owner_created_at') ORDER BY seqno",
                        cancellationToken));
                break;

            case DatabaseProvider.PostgreSql:
                Assert.Equal(
                    5L,
                    await ScalarInt64Async(
                        options,
                        "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'documents' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy','owner_issuer','owner_subject') AND data_type = 'bytea'",
                        cancellationToken));
                var indexDefinition = await ScalarStringAsync(
                    options,
                    "SELECT indexdef FROM pg_indexes WHERE tablename = 'documents' AND indexname = 'ix_documents_owner_created_at'",
                    cancellationToken);
                Assert.Contains("(owner_issuer, owner_subject, created_at_utc)", indexDefinition, StringComparison.Ordinal);
                Assert.DoesNotContain("UNIQUE", indexDefinition, StringComparison.OrdinalIgnoreCase);
                break;

            case DatabaseProvider.MySql:
            case DatabaseProvider.MariaDb:
                Assert.Equal(
                    5L,
                    await ScalarInt64Async(
                        options,
                        "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'documents' AND column_name IN ('created_by_issuer','created_by_subject','created_by_legacy','owner_issuer','owner_subject') AND data_type = 'varbinary'",
                        cancellationToken));
                Assert.Equal(
                    "owner_issuer,owner_subject,created_at_utc",
                    await ScalarStringAsync(
                        options,
                        "SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'documents' AND index_name = 'ix_documents_owner_created_at' AND non_unique = 1",
                        cancellationToken));
                Assert.Equal(
                    "Dynamic",
                    await ScalarStringAsync(
                        options,
                        "SELECT row_format FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'documents'",
                        cancellationToken),
                    ignoreCase: true);
                break;
        }
    }

    private static DbContextOptions<StructaDocDbContext> CreateOptions(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion)
    {
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
            ApplyMigrationsOnStartup = false,
        };
        var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
        return builder.Options;
    }

    private static async Task ResetAndMigrateAsync(
        DbContextOptions<StructaDocDbContext> options,
        string? targetMigration,
        CancellationToken cancellationToken)
    {
        await using var context = new StructaDocDbContext(options);
        await context.Database.EnsureDeletedAsync(cancellationToken);
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, cancellationToken);
    }

    private static async Task ExecuteAsync(
        DbContextOptions<StructaDocDbContext> options,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var context = new StructaDocDbContext(options);
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            AddParameters(command, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    private static async Task<long> ScalarInt64Async(
        DbContextOptions<StructaDocDbContext> options,
        string commandText,
        CancellationToken cancellationToken) => Convert.ToInt64(
            await ScalarAsync(options, commandText, cancellationToken));

    private static async Task<string> ScalarStringAsync(
        DbContextOptions<StructaDocDbContext> options,
        string commandText,
        CancellationToken cancellationToken) => Convert.ToString(
            await ScalarAsync(options, commandText, cancellationToken))!;

    private static async Task<object?> ScalarAsync(
        DbContextOptions<StructaDocDbContext> options,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var context = new StructaDocDbContext(options);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static void AddParameters(
        DbCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
        {
            count++;
        }

        return count;
    }

    private sealed class PdfDocumentTypeDetector : IDocumentTypeDetector
    {
        public Task<DetectedDocumentType?> DetectAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DetectedDocumentType?>(new("application/pdf", ".pdf"));
    }

    private sealed class ContractFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public async Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            var bytes = copy.ToArray();
            if (bytes.LongLength > maxBytes)
            {
                throw new FileSizeLimitExceededException(maxBytes);
            }

            files[storageRef] = bytes;
            return new StoredFile(
                storageRef,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            Stream content = new MemoryStream(files[storageRef], writable: false);
            return Task.FromResult(content);
        }

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            files.Remove(storageRef);
            return Task.CompletedTask;
        }
    }
}
