using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Persistence.Providers;

public static class ProviderConfigSeedExtensions
{
    /// <summary>
    /// The name the official endpoint is configured under. It is a plain identifier rather than a
    /// translated label, because an administrator who renames it must be able to recognize what
    /// they renamed.
    /// </summary>
    public const string OfficialProviderName = "official";

    /// <summary>
    /// What the hosted service calls <c>model_version</c>. The type's own default is the pipeline
    /// model; the endpoint this deployment ships is configured for the vision-language one.
    /// </summary>
    public const string OfficialProviderModel = "vlm";

    /// <summary>
    /// Configures the official MinerU endpoint on a deployment that has no Provider at all, so a
    /// first-run administrator supplies a token rather than assembling an address, a model, and a
    /// default marker from documentation. Deliberately without a credential: the token belongs to
    /// the deployment's own MinerU account and cannot be shipped in an image, so parsing stays
    /// refused, with a reason, until an administrator enters one.
    /// </summary>
    /// <remarks>
    /// Seeding is skipped as soon as any Provider exists, so it never competes with a configuration
    /// someone made. A deployment whose only Provider was deleted is configured again on the next
    /// start, which is the same state it would be in on a fresh volume.
    /// </remarks>
    public static async Task SeedStructaDocOfficialProviderAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("StructaDoc.ProviderConfigSeed");
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();

        if (await dbContext.ProviderConfigs.AnyAsync(cancellationToken))
        {
            return;
        }

        if (!ProviderConfigDefinition.TryCreate(
                OfficialProviderName,
                ProviderTypes.MinerUCloud,
                ProviderTypeDescriptors.MinerUCloudBaseUrl,
                OfficialProviderModel,
                backend: null,
                credential: null,
                clearCredential: false,
                isEnabled: true,
                isDefault: true,
                out var definition,
                out var field,
                out var message))
        {
            logger.LogError(
                "The official Provider configuration is not valid and was not created. {Field}: {Message}",
                field,
                message);
            return;
        }

        var service = scope.ServiceProvider
            .GetRequiredService<IProviderConfigAdministrationService>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var result = await service.CreateAsync(
            definition!,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        if (result.Status == ProviderConfigMutationStatus.Succeeded)
        {
            logger.LogInformation(
                "Configured the official Provider {ProviderConfigId} without a credential. Parsing stays unavailable until an administrator supplies one.",
                result.Config!.Id);
            return;
        }

        // Another instance starting against the same database gets there first, which is the
        // expected outcome rather than a fault: the configuration exists either way.
        logger.LogInformation(
            "The official Provider was already configured by another instance.");
    }
}
