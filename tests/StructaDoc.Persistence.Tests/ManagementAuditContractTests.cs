using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.Relational.Stores;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Settings;
using StructaDoc.Application.Settings;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// The management audit persistence contract from #104: the library's writer stages rows without
/// saving, the sensitive-content policy rejects or redacts secret-shaped audit content, a settings
/// write commits its audit row and its setting row as one unit, and the control-plane migration
/// creates the audit table through the ordinary migration entry.
/// </summary>
public sealed class ManagementAuditContractTests
{
    [Fact]
    public async Task Writer_stages_rows_without_saving()
    {
        using var deployment = new ControlPlaneDeployment();
        await deployment.MigrateAsync();

        var auditEvent = ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                Guid.NewGuid().ToString("D"),
                "staging-admin"),
            WellKnownManagementAuditActions.AdminLoginSucceeded,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.AdminSession,
                Guid.NewGuid().ToString("D")),
            ManagementAuditOutcome.Success);

        var writer = new EfCoreManagementAuditWriter<ControlPlaneDbContext>(deployment.DbContext);
        await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);

        // The row is in the change tracker and nowhere else: no save, no file content.
        Assert.Single(deployment.DbContext.ChangeTracker.Entries());
        Assert.Equal(0, await deployment.CountAuditRowsAsync());
    }

    [Fact]
    public async Task A_metadata_key_that_names_a_secret_is_rejected()
    {
        using var deployment = new ControlPlaneDeployment();
        await deployment.MigrateAsync();

        var error = Assert.Throws<ManagementAuditException>(() => ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                Guid.NewGuid().ToString("D"),
                "rejected-admin"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.Configuration,
                SettingCatalog.StorageRootPath),
            ManagementAuditOutcome.Success,
            metadata: new Dictionary<string, string>
            {
                ["password"] = "hunter2",
            }));

        Assert.Equal("audit.metadata_key_rejected", error.ErrorCode);
    }

    [Fact]
    public async Task A_secret_shaped_metadata_value_is_redacted_whole()
    {
        var auditEvent = ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                Guid.NewGuid().ToString("D"),
                "redacted-admin"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.Configuration,
                SettingCatalog.StorageRootPath),
            ManagementAuditOutcome.Success,
            metadata: new Dictionary<string, string>
            {
                ["detail"] = "password=hunter2",
            });

        // An assignment this layer cannot parse the end of fails closed for the whole field.
        Assert.Equal("[REDACTED]", auditEvent.Metadata["detail"]);
    }

    [Fact]
    public async Task A_connection_string_shaped_value_is_redacted()
    {
        var auditEvent = ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                Guid.NewGuid().ToString("D"),
                "redacted-admin"),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.Configuration,
                SettingCatalog.DatabaseConnectionString),
            ManagementAuditOutcome.Success,
            securityDescription: "Server=db.example.test;Database=structadoc;User ID=structadoc");

        Assert.Equal("[REDACTED]", auditEvent.SecurityDescription);
    }

    [Fact]
    public async Task A_pem_key_block_is_redacted()
    {
        const string pem = """
            -----BEGIN RSA PRIVATE KEY-----
            MIIEpAIBAAKCAQEA0123456789
            -----END RSA PRIVATE KEY-----
            """;
        var auditEvent = ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                Guid.NewGuid().ToString("D"),
                "redacted-admin"),
            WellKnownManagementAuditActions.AdminLoginSucceeded,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.AdminSession,
                Guid.NewGuid().ToString("D")),
            ManagementAuditOutcome.Success,
            securityDescription: $"attached key follows {pem}");

        // The PEM block is redacted in place: the surrounding sentence survives, the key does not.
        Assert.Equal("attached key follows [REDACTED_KEY]", auditEvent.SecurityDescription);
    }

    [Fact]
    public async Task A_setting_write_commits_its_audit_row_and_its_setting_row_together()
    {
        using var deployment = new ControlPlaneDeployment();
        await deployment.MigrateAsync();
        var configuration = StructaDocSettingsConfiguration.Create(
            new ConfigurationBuilder().Build(),
            deployment.Options,
            [],
            new FakeSettingSecretProtector(),
            new SettingsStartupFault());
        var settings = new SettingsService(
            deployment.DbContext,
            configuration,
            new FakeSettingSecretProtector(),
            deployment.AuditRecorder,
            []);

        var result = await settings.SetAsync(
            SettingCatalog.UploadApiEnabled,
            "false",
            new SettingActor(Guid.NewGuid().ToString("D"), "auditing-admin"),
            DateTime.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteStatus.Succeeded, result.Status);
        Assert.Equal(1, await deployment.CountAuditRowsAsync());
        var row = await deployment.ReadSingleAuditRowAsync();
        Assert.Equal("configuration.changed", row.Action);
        Assert.Equal(1, row.Outcome);
        Assert.Equal(SettingCatalog.UploadApiEnabled, row.TargetId);
        Assert.Equal("auditing-admin", row.OperatorDisplayName);
        Assert.Equal("false", await deployment.ReadSettingValueAsync(SettingCatalog.UploadApiEnabled));
    }

    [Fact]
    public async Task A_setting_write_rolls_back_with_its_audit_row_when_the_save_fails()
    {
        using var deployment = new ControlPlaneDeployment();
        await deployment.MigrateThroughOrdinaryEntryAsync();

        // The interceptor fails the write's save, after both the setting row and the audit row were
        // staged on the same context. Neither may survive.
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        ControlPlaneServiceCollectionExtensions.ConfigureControlPlane(options, deployment.Options);
        options.AddInterceptors(new FailingSaveInterceptor());
        await using var dbContext = new ControlPlaneDbContext(options.Options);
        var configuration = StructaDocSettingsConfiguration.Create(
            new ConfigurationBuilder().Build(),
            deployment.Options,
            [],
            new FakeSettingSecretProtector(),
            new SettingsStartupFault());
        var settings = new SettingsService(
            dbContext,
            configuration,
            new FakeSettingSecretProtector(),
            new StructaDocManagementAuditRecorder(
                new EfCoreManagementAuditWriter<ControlPlaneDbContext>(dbContext),
                dbContext),
            []);

        await Assert.ThrowsAsync<FailingSaveInterceptor.SaveFailedException>(
            () => settings.SetAsync(
                SettingCatalog.UploadApiEnabled,
                "false",
                new SettingActor(Guid.NewGuid().ToString("D"), "auditing-admin"),
                DateTime.UtcNow,
                TestContext.Current.CancellationToken));

        await using var verifier = deployment.CreateVerifierContext();
        Assert.Equal(0, await verifier.CountRowsAsync("SELECT COUNT(*) FROM settings"));
        Assert.Equal(0, await verifier.CountRowsAsync("SELECT COUNT(*) FROM service_audit_logs"));
    }

    [Fact]
    public async Task The_control_plane_migration_creates_the_audit_table_through_the_ordinary_entry()
    {
        using var deployment = new ControlPlaneDeployment();

        // Before the migration, neither the table nor the file exists.
        Assert.False(File.Exists(deployment.Options.DatabasePath));

        await deployment.MigrateThroughOrdinaryEntryAsync();

        Assert.True(File.Exists(deployment.Options.DatabasePath));
        var columns = await deployment.ReadAuditColumnsAsync();
        Assert.Contains("action", columns);
        Assert.Contains("target_type", columns);
        Assert.Contains("target_id", columns);
        Assert.Contains("outcome", columns);
        Assert.Contains("operator_id", columns);
        Assert.Contains("operator_source", columns);
        Assert.Contains("metadata_json", columns);
        Assert.Equal(
            1,
            await deployment.CountRowsAsync(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'service_audit_logs'"));
    }

    private sealed class ControlPlaneDeployment : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-audit-tests",
            Guid.NewGuid().ToString("N"));

        public ControlPlaneDeployment()
        {
            Directory.CreateDirectory(directory);
            Options = new ControlPlaneOptions
            {
                DatabasePath = Path.Combine(directory, "control.db"),
            };
            var services = new ServiceCollection();
            services.AddStructaDocControlPlane(Options);
            Provider = services.BuildServiceProvider();
        }

        public ControlPlaneOptions Options { get; }

        public ControlPlaneDbContext DbContext =>
            Provider.GetRequiredService<ControlPlaneDbContext>();

        public StructaDocManagementAuditRecorder AuditRecorder =>
            Provider.GetRequiredService<StructaDocManagementAuditRecorder>();

        private ServiceProvider Provider { get; }

        // The same entry Program.cs and the migration command use.
        public async Task MigrateThroughOrdinaryEntryAsync() =>
            await Provider.ApplyStructaDocControlPlaneMigrationsAsync(
                TestContext.Current.CancellationToken);

        // Kept distinct from the ordinary entry for tests that need the raw context behavior.
        public async Task MigrateAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        public ControlPlaneDbContext CreateVerifierContext()
        {
            var builder = new DbContextOptionsBuilder<ControlPlaneDbContext>();
            ControlPlaneServiceCollectionExtensions.ConfigureControlPlane(builder, Options);
            return new ControlPlaneDbContext(builder.Options);
        }

        public async Task<long> CountAuditRowsAsync()
        {
            await using var verifier = CreateVerifierContext();
            return await verifier.CountRowsAsync("SELECT COUNT(*) FROM service_audit_logs");
        }

        public async Task<(string Action, int Outcome, string TargetId, string? OperatorDisplayName)>
            ReadSingleAuditRowAsync()
        {
            await using var verifier = CreateVerifierContext();
            await using var command = verifier.Database.GetDbConnection().CreateCommand();
            await verifier.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            command.CommandText = """
                SELECT action, outcome, target_id, operator_display_name
                FROM service_audit_logs
                """;
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            var row = (
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
            return row;
        }

        public async Task<string?> ReadSettingValueAsync(string key)
        {
            await using var verifier = CreateVerifierContext();
            await using var command = verifier.Database.GetDbConnection().CreateCommand();
            await verifier.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$key";
            parameter.Value = key;
            command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value);
        }

        public async Task<List<string>> ReadAuditColumnsAsync()
        {
            await using var verifier = CreateVerifierContext();
            await using var command = verifier.Database.GetDbConnection().CreateCommand();
            await verifier.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            command.CommandText = "PRAGMA table_info('service_audit_logs')";
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

        public async Task<long> CountRowsAsync(string commandText)
        {
            await using var verifier = CreateVerifierContext();
            return await verifier.CountRowsAsync(commandText);
        }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Fails the first save it sees, so a staged write and its audit row roll back.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            throw new SaveFailedException();
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new SaveFailedException();
        }

        public sealed class SaveFailedException : Exception
        {
            public SaveFailedException()
                : base("The test interceptor failed the save after both rows were staged.")
            {
            }
        }
    }
}

file static class ControlPlaneDbContextQueryExtensions
{
    public static async Task<long> CountRowsAsync(
        this ControlPlaneDbContext verifier,
        string commandText)
    {
        await using var command = verifier.Database.GetDbConnection().CreateCommand();
        await verifier.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        command.CommandText = commandText;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
