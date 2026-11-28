using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;

namespace StructaDoc.Adapters.ProviderResults;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProviderResults(
        this IServiceCollection services,
        ResultIntakeOptions intakeOptions,
        ResultNormalizationOptions? normalizationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(intakeOptions);
        intakeOptions.Validate();
        normalizationOptions ??= new ResultNormalizationOptions();
        normalizationOptions.Validate();

        services.AddSingleton(intakeOptions);
        services.AddSingleton(normalizationOptions);
        services.AddSingleton<IResultIntake, StoredResultIntake>();
        services.AddSingleton<IResultNormalizer, MinerUResultNormalizer>();
        // Printed page labels are not Provider output: one Provider-neutral step adds them to
        // every normalized bundle, so the Provider interface stays unchanged.
        services.AddSingleton<IPdfPageLabelReader, PdfSharpPageLabelReader>();
        services.AddSingleton<PageLabelEnricher>();
        return services;
    }
}
