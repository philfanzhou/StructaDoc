using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.ProviderResults;

namespace StructaDoc.Infrastructure.ProviderResults;

public static class ProviderResultServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocProviderResults(
        this IServiceCollection services,
        ProviderResultIntakeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IProviderResultIntake, StoredProviderResultIntake>();
        return services;
    }
}
