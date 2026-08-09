using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Application.Documents;
using StructaDoc.Platform.Documents;
using StructaDoc.Platform.Persistence;
using StructaDoc.Platform.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class DocumentIngestionCompensationTests
{
    [Fact]
    public async Task Database_failure_removes_the_already_stored_original()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-ingestion-tests",
            Guid.NewGuid().ToString("N"));
        var storagePath = Path.Combine(testDirectory, "storage");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var dbContextOptions = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(testDirectory, "unmigrated.db")};Pooling=False")
                .Options;
            await using var dbContext = new StructaDocDbContext(dbContextOptions);
            var storage = new LocalFileStorage(new FileStorageOptions
            {
                RootPath = storagePath,
            });
            var service = new EfCoreDocumentIngestionService(
                dbContext,
                storage,
                new OfficeDocumentTypeDetector(),
                new DocumentIngestionOptions(),
                TimeProvider.System,
                NullLogger<EfCoreDocumentIngestionService>.Instance);
            await using var content = new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray());

            await Assert.ThrowsAsync<DbUpdateException>(() => service.IngestAsync(
                new DocumentIngestionRequest("sample.pdf", "application/pdf", content)));

            Assert.Empty(Directory.GetFiles(storagePath, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
