using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Conversion;

namespace StructaDoc.Platform.Conversion;

public static class ConversionServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocDocumentConversion(
        this IServiceCollection services,
        LibreOfficeConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<ILibreOfficeProcessRunner, LibreOfficeProcessRunner>();
        services.AddSingleton<IDocumentConverter, LibreOfficeDocumentConverter>();
        return services;
    }
}
