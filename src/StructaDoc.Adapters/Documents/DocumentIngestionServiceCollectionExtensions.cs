using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;

namespace StructaDoc.Adapters.Documents;

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
        if (string.Equals(storageOptions.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var config = new AmazonS3Config { ForcePathStyle = storageOptions.ForcePathStyle };
                if (!string.IsNullOrWhiteSpace(storageOptions.ServiceUrl))
                {
                    config.ServiceURL = storageOptions.ServiceUrl;
                    config.AuthenticationRegion = storageOptions.Region ?? "us-east-1";
                }
                else config.RegionEndpoint = RegionEndpoint.GetBySystemName(storageOptions.Region ?? "us-east-1");
                return storageOptions.AccessKey is null
                    ? new AmazonS3Client(config)
                    : new AmazonS3Client(new BasicAWSCredentials(storageOptions.AccessKey, storageOptions.SecretKey), config);
            });
            services.AddSingleton<IFileStorage, S3FileStorage>();
        }
        else services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IDocumentTypeDetector, OfficeDocumentTypeDetector>();
        services.AddScoped<IDocumentIngestionService, EfCoreDocumentIngestionService>();
        services.AddScoped<IDocumentAuthorizationService, EfCoreDocumentAuthorizationService>();
        services.AddScoped<IDocumentReadService, EfCoreDocumentReadService>();
        var health = services.AddHealthChecks();
        if (string.Equals(storageOptions.Provider, "S3", StringComparison.OrdinalIgnoreCase)) health.AddCheck<S3FileStorageHealthCheck>("file-storage", tags: ["ready"]);
        else health.AddCheck<LocalFileStorageHealthCheck>("file-storage", tags: ["ready"]);
        return services;
    }
}
