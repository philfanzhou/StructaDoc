using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class InnoDbMigrationPreflightTests
{
    private static readonly InnoDbIndexMigrationRequirement Requirement =
        Assert.Single(
            InnoDbIndexMigrationRegistry.Requirements,
            requirement => requirement.MigrationSuffix == "_AddProviderConfigsAndParseCreation");

    [Theory]
    [InlineData("20260805115926_AddProviderConfigsAndParseCreation")]
    [InlineData("20260805115933_AddProviderConfigsAndParseCreation")]
    public void Registry_matches_provider_specific_migration_ids(string migrationId)
    {
        var match = Assert.Single(
            InnoDbIndexMigrationRegistry.FindPendingRequirements([migrationId]));

        Assert.Equal("parse_runs", match.TableName);
        Assert.Equal("ux_parse_runs_idempotency", match.IndexName);
    }

    [Theory]
    [InlineData("20260830160429_MigrateDocumentCanonicalActorIdentity")]
    public void Registry_matches_document_identity_migration_ids(string migrationId)
    {
        var match = Assert.Single(
            InnoDbIndexMigrationRegistry.FindPendingRequirements([migrationId]));

        Assert.Equal("documents", match.TableName);
        Assert.Equal("ix_documents_owner_created_at", match.IndexName);
    }

    [Theory]
    [InlineData("20260830165343_MigrateAccessGrantCanonicalActorIdentity")]
    public void Registry_matches_access_grant_identity_migration_ids(string migrationId)
    {
        var match = Assert.Single(
            InnoDbIndexMigrationRegistry.FindPendingRequirements([migrationId]));

        Assert.Equal("document_access_grants", match.TableName);
        Assert.Equal("ux_document_access_grants_principal", match.IndexName);
    }

    [Theory]
    [InlineData("20260830172615_MigrateParseRunCanonicalActorIdentity")]
    public void Registry_matches_parse_run_identity_migration_ids(string migrationId)
    {
        var match = Assert.Single(
            InnoDbIndexMigrationRegistry.FindPendingRequirements([migrationId]));

        Assert.Equal("parse_runs", match.TableName);
        Assert.Equal("ux_parse_runs_idempotency", match.IndexName);
    }

    [Fact]
    public void Registry_does_not_gate_unrelated_pending_migrations()
    {
        Assert.Empty(InnoDbIndexMigrationRegistry.FindPendingRequirements(
            ["20260821021427_AddDocumentConcurrencyGate"]));
    }

    [Fact]
    public void Page_size_failure_is_actionable()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => InnoDbMigrationPreflight.ValidatePageSize(8192));

        Assert.Contains("innodb_page_size=8192", error.Message, StringComparison.Ordinal);
        Assert.Contains("at least 16384", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Compact")]
    [InlineData("REDUNDANT")]
    public void Existing_table_row_format_failure_is_actionable(string rowFormat)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => InnoDbMigrationPreflight.ValidateTableRowFormat(Requirement, rowFormat));

        Assert.Contains("parse_runs", error.Message, StringComparison.Ordinal);
        Assert.Contains($"ROW_FORMAT={rowFormat}", error.Message, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Future_table_default_failure_is_actionable()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => InnoDbMigrationPreflight.ValidateDefaultRowFormat(Requirement, "COMPACT"));

        Assert.Contains("innodb_default_row_format=COMPACT", error.Message, StringComparison.Ordinal);
        Assert.Contains("Set innodb_default_row_format=DYNAMIC", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_innodb_providers_do_not_open_a_preflight_connection()
    {
        var result = await new InnoDbMigrationPreflight().CheckAsync(
            new DatabaseOptions
            {
                Provider = DatabaseProvider.PostgreSql,
                ConnectionString = "Server=unreachable.invalid;Database=structadoc;User Id=none;Password=none",
                ApplyMigrationsOnStartup = false,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.DatabaseExists);
        Assert.Empty(result.PendingMigrations);
        Assert.False(result.RequiresInnoDbValidation);
    }
}
