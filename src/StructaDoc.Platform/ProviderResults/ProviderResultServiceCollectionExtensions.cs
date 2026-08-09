using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.ProviderResults;

namespace StructaDoc.Platform.ProviderResults;

public static class ProviderResultServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocProviderResults(
        this IServiceCollection services,
        ProviderResultIntakeOptions intakeOptions,
        ProviderResultNormalizationOptions? normalizationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(intakeOptions);
        intakeOptions.Validate();
        normalizationOptions ??= new ProviderResultNormalizationOptions();
        normalizationOptions.Validate();

        services.AddSingleton(intakeOptions);
        services.AddSingleton(normalizationOptions);
        services.AddSingleton<IProviderResultIntake, StoredProviderResultIntake>();
        services.AddSingleton<IProviderResultNormalizer, MinerUResultNormalizer>();
        return services;
    }
}
