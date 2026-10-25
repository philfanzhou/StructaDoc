using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Application.Providers;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

// The per-request cloud layout configuration snapshot: which Provider configuration it reads,
// which immutable version it captures, and the explicit state it answers for every deployment
// shape that cannot serve the exchange. The credential round-trips through the secret protector
// the same way the Parse Run execution context's does.
public sealed class LayoutCloudConfigurationSourceTests
{
    [Fact]
    public async Task A_ready_default_cloud_configuration_loads_with_its_current_version_and_credential()
    {
        await using var database = await SourceTestDatabase.CreateAsync(
            providerType: "mineru-cloud",
            isEnabled: true,
            isDefault: true,
            withCredential: true);

        var snapshot = await new EfCoreLayoutCloudConfigurationSource(
            database.Context,
            new ReversingSecretProtector()).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LayoutCloudConfigurationState.Ready, snapshot.State);
        var configuration = snapshot.Configuration!;
        Assert.Equal(database.ProviderConfigId, configuration.ConfigId);
        Assert.Equal(database.ProviderVersionId, configuration.VersionId);
        Assert.Equal(ProviderTypes.MinerUCloud, configuration.ProviderType);
        Assert.Equal(new Uri("https://mineru.example/"), configuration.BaseUri);
        Assert.Equal("vlm", configuration.Model);
        // Decrypted, not the protected form the row stores.
        Assert.Equal("cloud-token", configuration.Credential!.Reveal());
        Assert.Contains("[redacted]", configuration.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_default_answers_no_default_provider()
    {
        await using var database = await SourceTestDatabase.CreateAsync(
            providerType: "mineru-cloud",
            isEnabled: true,
            isDefault: false,
            withCredential: true);

        var snapshot = await LoadAsync(database);

        Assert.Equal(LayoutCloudConfigurationState.NoDefaultProvider, snapshot.State);
        Assert.Null(snapshot.Configuration);
    }

    [Theory]
    [InlineData("mineru-local", LayoutCloudConfigurationState.DefaultProviderNotCloud)]
    [InlineData("another-type", LayoutCloudConfigurationState.DefaultProviderNotCloud)]
    public async Task A_non_cloud_default_answers_not_cloud(
        string providerType,
        LayoutCloudConfigurationState expected)
    {
        await using var database = await SourceTestDatabase.CreateAsync(
            providerType: providerType,
            isEnabled: true,
            isDefault: true,
            withCredential: true);

        Assert.Equal(expected, (await LoadAsync(database)).State);
    }

    [Fact]
    public async Task A_disabled_default_answers_disabled()
    {
        await using var database = await SourceTestDatabase.CreateAsync(
            providerType: "mineru-cloud",
            isEnabled: false,
            isDefault: true,
            withCredential: true);

        var snapshot = await LoadAsync(database);

        Assert.Equal(LayoutCloudConfigurationState.DefaultProviderDisabled, snapshot.State);
    }

    [Fact]
    public async Task A_default_without_a_credential_answers_credential_missing()
    {
        // The shipped state of a fresh deployment: the seeded official Provider exists, is enabled
        // and default, and waits for its token.
        await using var database = await SourceTestDatabase.CreateAsync(
            providerType: "mineru-cloud",
            isEnabled: true,
            isDefault: true,
            withCredential: false);

        var snapshot = await LoadAsync(database);

        Assert.Equal(LayoutCloudConfigurationState.CredentialMissing, snapshot.State);
        Assert.Contains("credential", snapshot.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_states_carry_their_configuration_invariant()
    {
        Assert.Throws<ArgumentException>(
            () => new LayoutCloudConfigurationSnapshot(LayoutCloudConfigurationState.Ready));
        Assert.Throws<ArgumentException>(
            () => new LayoutCloudConfigurationSnapshot(
                LayoutCloudConfigurationState.CredentialMissing,
                ReadyConfiguration()));
    }

    private static async Task<LayoutCloudConfigurationSnapshot> LoadAsync(SourceTestDatabase database) =>
        await new EfCoreLayoutCloudConfigurationSource(
            database.Context,
            new ReversingSecretProtector()).LoadAsync(TestContext.Current.CancellationToken);

    private static ProviderExecutionConfiguration ReadyConfiguration() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ProviderTypes.MinerUCloud,
        new Uri("https://mineru.example/"),
        "vlm",
        null,
        new ProviderCredential("cloud-token"));

    /// <summary>
    /// A stand-in for the Data Protection protector: protected text is the plaintext reversed, so
    /// the test proves the snapshot decrypts rather than copies the stored column.
    /// </summary>
    private sealed class ReversingSecretProtector : IProviderSecretProtector
    {
        public string Protect(string plaintext) => new string(plaintext.Reverse().ToArray());

        public string Unprotect(string protectedValue) => new string(protectedValue.Reverse().ToArray());
    }

    private sealed class SourceTestDatabase : IAsyncDisposable
    {
        private SourceTestDatabase(
            string directory,
            StructaDocDbContext context,
            Guid providerConfigId,
            Guid providerVersionId)
        {
            Directory = directory;
            Context = context;
            ProviderConfigId = providerConfigId;
            ProviderVersionId = providerVersionId;
        }

        private string Directory { get; }

        public StructaDocDbContext Context { get; }

        public Guid ProviderConfigId { get; }

        public Guid ProviderVersionId { get; }

        public static async Task<SourceTestDatabase> CreateAsync(
            string providerType,
            bool isEnabled,
            bool isDefault,
            bool withCredential)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "structadoc-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directory, "source.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;

            var providerConfigId = Guid.NewGuid();
            var providerVersionId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;
            var context = new StructaDocDbContext(options);
            await context.Database.MigrateAsync();
            context.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerConfigId,
                Name = "Source Test Provider",
                ProviderType = providerType,
                IsEnabled = isEnabled,
                DefaultMarker = isDefault ? "default" : null,
                CurrentVersionId = providerVersionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            context.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = providerVersionId,
                ProviderConfigId = providerConfigId,
                VersionNumber = 1,
                BaseUrl = "https://mineru.example/",
                Model = "vlm",
                Backend = null,
                ProtectedCredential = withCredential
                    ? new string("cloud-token".Reverse().ToArray())
                    : null,
                CreatedAtUtc = nowUtc,
            });
            await context.SaveChangesAsync();
            return new SourceTestDatabase(directory, context, providerConfigId, providerVersionId);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
