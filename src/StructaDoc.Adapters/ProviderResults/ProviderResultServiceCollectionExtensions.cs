using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;

namespace StructaDoc.Adapters.ProviderResults;

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
        // Printed page labels are not Provider output: one Provider-neutral step adds them to
        // every normalized bundle, so the Provider interface stays unchanged.
        services.AddSingleton<IPdfPageLabelReader, PdfSharpPageLabelReader>();
        services.AddSingleton<PageLabelEnricher>();
        return services;
    }
}
