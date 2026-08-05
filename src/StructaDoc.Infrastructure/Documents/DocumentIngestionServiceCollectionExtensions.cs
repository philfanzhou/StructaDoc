using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;
using StructaDoc.Infrastructure.Storage;

namespace StructaDoc.Infrastructure.Documents;

public static class DocumentIngestionServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocDocumentIngestion(
        this IServiceCollection services,
        DocumentIngestionOptions ingestionOptions,
        FileStorageOptions storageOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ingestionOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ingestionOptions.Validate();
        storageOptions.Validate();

        services.AddSingleton(ingestionOptions);
        services.AddSingleton(storageOptions);
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IDocumentTypeDetector, OfficeDocumentTypeDetector>();
        services.AddScoped<IDocumentIngestionService, EfCoreDocumentIngestionService>();
        services
            .AddHealthChecks()
            .AddCheck<LocalFileStorageHealthCheck>(
                "file-storage",
                tags: ["ready"]);
        return services;
    }
}
