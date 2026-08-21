using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class ParseRunCreationGuardTests
{
    [Fact]
    public async Task Creation_wins_stale_document_and_provider_deletions_atomically()
    {
        await using var database = await GuardTestDatabase.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var staleDocumentContext = new StructaDocDbContext(database.Options);
        await using var staleProviderContext = new StructaDocDbContext(database.Options);
        var staleDocument = await staleDocumentContext.Documents.SingleAsync(cancellationToken);
        var staleProvider = await staleProviderContext.ProviderConfigs.SingleAsync(cancellationToken);

        ParseRunCreationResult creation;
        await using (var creationContext = new StructaDocDbContext(database.Options))
        {
            creation = await new EfCoreParseRunService(creationContext).CreateAsync(
                database.CreateRequest(),
                cancellationToken);
        }

        Assert.Equal(ParseRunCreationStatus.Created, creation.Status);

        staleDocument.LifecycleState = ResourceLifecycleStates.DeletionPending;
        staleDocument.DeletionRequestedAtUtc = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleDocumentContext.SaveChangesAsync(cancellationToken));

        await using (var transaction = await staleProviderContext.Database.BeginTransactionAsync(
            cancellationToken))
        {
            await staleProviderContext.ProviderConfigVersions
                .Where(version => version.ProviderConfigId == staleProvider.Id)
                .ExecuteDeleteAsync(cancellationToken);
            staleProviderContext.ProviderConfigs.Remove(staleProvider);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => staleProviderContext.SaveChangesAsync(cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
        }

        await using var verification = new StructaDocDbContext(database.Options);
        Assert.Single(await verification.ParseRuns.AsNoTracking().ToListAsync(cancellationToken));
        Assert.Single(await verification.ProviderConfigs.AsNoTracking().ToListAsync(cancellationToken));
        Assert.Single(await verification.ProviderConfigVersions.AsNoTracking().ToListAsync(cancellationToken));
        Assert.Equal(
            ResourceLifecycleStates.Active,
            await verification.Documents.Select(document => document.LifecycleState)
                .SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task Creation_refuses_a_document_whose_deletion_already_won()
    {
        await using var database = await GuardTestDatabase.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var dbContext = new StructaDocDbContext(database.Options);
        await dbContext.Documents.ExecuteUpdateAsync(
            updates => updates
                .SetProperty(
                    document => document.LifecycleState,
                    ResourceLifecycleStates.DeletionPending)
                .SetProperty(
                    document => document.ConcurrencyVersion,
                    document => document.ConcurrencyVersion + 1),
            cancellationToken);

        var result = await new EfCoreParseRunService(dbContext).CreateAsync(
            database.CreateRequest(),
            cancellationToken);

        Assert.Equal(ParseRunCreationStatus.DocumentNotFound, result.Status);
        Assert.Empty(await dbContext.ParseRuns.AsNoTracking().ToListAsync(cancellationToken));
    }

    private sealed class GuardTestDatabase : IAsyncDisposable
    {
        private GuardTestDatabase(
            string directory,
            DbContextOptions<StructaDocDbContext> options,
            Guid documentId,
            Guid providerConfigId)
        {
            Directory = directory;
            Options = options;
            DocumentId = documentId;
            ProviderConfigId = providerConfigId;
        }

        private string Directory { get; }
        private Guid DocumentId { get; }
        private Guid ProviderConfigId { get; }
        public DbContextOptions<StructaDocDbContext> Options { get; }

        public static async Task<GuardTestDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "structadoc-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directory, "guard.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;
            var documentId = Guid.NewGuid();
            var providerConfigId = Guid.NewGuid();
            var providerVersionId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync();
            dbContext.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "guard.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('a', 64),
                StorageRef = "documents/guard.pdf",
                CreatedAtUtc = nowUtc,
            });
            var provider = new ProviderConfigEntity
            {
                Id = providerConfigId,
                Name = "Guard Provider",
                ProviderType = "mineru-local",
                IsEnabled = true,
                DefaultMarker = "default",
                CurrentVersionId = providerVersionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            dbContext.ProviderConfigs.Add(provider);
            dbContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = providerVersionId,
                ProviderConfigId = providerConfigId,
                ProviderConfig = provider,
                VersionNumber = 1,
                BaseUrl = "http://provider.test/",
                CreatedAtUtc = nowUtc,
            });
            await dbContext.SaveChangesAsync();
            return new GuardTestDatabase(directory, options, documentId, providerConfigId);
        }

        public ParseRunCreateRequest CreateRequest() => new(
            DocumentId,
            ProviderConfigId,
            "{}",
            3,
            "test-actor",
            null,
            DateTime.UtcNow);

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
