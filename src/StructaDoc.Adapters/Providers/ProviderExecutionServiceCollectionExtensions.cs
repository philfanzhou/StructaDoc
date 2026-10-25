using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

public static class ProviderExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocParseProviders(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => new ProviderHttpClientOwner(
            CreateProviderApiHttpClient(),
            CreateSignedTransferHttpClient()));
        services.AddSingleton(serviceProvider => new MinerUCloudParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().ProviderApiClient,
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().SignedTransferClient));
        services.AddSingleton(serviceProvider => new MinerULocalParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().ProviderApiClient));
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerUCloudParseProvider>());
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerULocalParseProvider>());
        return services;
    }

    /// <summary>
    /// Registers the layout-detection execution contract for the bound <paramref name="layoutOptions"/>
    /// and <paramref name="options"/>.
    ///
    /// The backend choice is decided here, once, because it is a restart-bound administrator setting:
    /// the default <c>layout-runtime</c> keeps the exact shipped behavior (with no runtime configured,
    /// no <see cref="ILayoutDetectionProvider"/> implementation is registered at all, which is what
    /// makes the capability resolution answer "unsupported" instead of the service guessing an
    /// endpoint), and an explicit <c>mineru-cloud</c> registers the cloud adapter, which resolves its
    /// Provider configuration per request through <see cref="ILayoutCloudConfigurationSource"/>. The
    /// two implementations are never registered together, and nothing registers the cloud adapter
    /// implicitly: selecting it is the one deliberate act.
    ///
    /// The cloud adapter shares the parse providers' HTTP clients — one Provider API client carrying
    /// the credential and one SSRF-guarded signed-transfer client that never does — creating its own
    /// pair only when it is registered standalone, and it is scoped because its configuration source
    /// reads the request-scoped business database context.
    /// </summary>
    public static IServiceCollection AddStructaDocLayoutDetection(
        this IServiceCollection services,
        LayoutRuntimeOptions options,
        LayoutOptions layoutOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layoutOptions);
        options.Validate();
        layoutOptions.Validate();

        var runtime = options.ToRuntimeConfiguration();
        // The bound configurations are registered for the endpoint that resolves per request: the
        // public layout route reads them and hands them to the capability resolution, so what the
        // endpoint resolves with is exactly what was bound here, restart to restart.
        services.AddSingleton(runtime);
        if (layoutOptions.ToBackend() == LayoutBackend.MinerUCloud)
        {
            services.AddSingleton(LayoutBackendSelection.ForCloud());
            services.TryAddSingleton(_ => new ProviderHttpClientOwner(
                CreateProviderApiHttpClient(),
                CreateSignedTransferHttpClient()));
            services.AddScoped<ILayoutCloudConfigurationSource, EfCoreLayoutCloudConfigurationSource>();
            services.AddScoped<ILayoutDetectionProvider>(serviceProvider =>
                new MinerUCloudLayoutDetectionProvider(
                    serviceProvider.GetRequiredService<ProviderHttpClientOwner>().ProviderApiClient,
                    serviceProvider.GetRequiredService<ProviderHttpClientOwner>().SignedTransferClient,
                    serviceProvider.GetRequiredService<ILayoutCloudConfigurationSource>()));
            services.AddScoped<ILayoutDetectionProviderResolver>(serviceProvider =>
                new LayoutDetectionProviderResolver(
                    serviceProvider.GetServices<ILayoutDetectionProvider>()));
            return services;
        }

        services.AddSingleton(LayoutBackendSelection.ForRuntime(runtime));
        if (runtime.BaseUri is null)
        {
            services.AddSingleton<ILayoutDetectionProviderResolver>(_ =>
                new LayoutDetectionProviderResolver(Enumerable.Empty<ILayoutDetectionProvider>()));
            return services;
        }

        var layoutRuntimeClient = CreateHttpClient(connectCallback: null);
        layoutRuntimeClient.Timeout = runtime.RequestTimeout;
        services.AddSingleton(_ => new LayoutRuntimeHttpClientOwner(layoutRuntimeClient));
        services.AddSingleton<ILayoutDetectionProvider>(serviceProvider =>
            new MinerULayoutDetectionProvider(
                serviceProvider.GetRequiredService<LayoutRuntimeHttpClientOwner>().Client,
                runtime));
        services.AddSingleton<ILayoutDetectionProviderResolver>(serviceProvider =>
            new LayoutDetectionProviderResolver(
                serviceProvider.GetServices<ILayoutDetectionProvider>()));
        return services;
    }

    private static HttpClient CreateProviderApiHttpClient()
    {
        return CreateHttpClient(connectCallback: null);
    }

    private static HttpClient CreateSignedTransferHttpClient()
    {
        return CreateHttpClient(SignedTransferDestinationPolicy.ConnectAsync);
    }

    private static HttpClient CreateHttpClient(
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connectCallback)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = connectCallback is null,
            ConnectCallback = connectCallback,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    private sealed class LayoutRuntimeHttpClientOwner(HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
        }
    }
}

/// <summary>
/// The two HTTP clients the cloud protocol needs, owned once per host: the Provider API client that
/// carries the credential and the signed-transfer client that must never see it. The layout cloud
/// adapter shares them with the full-parsing cloud adapter so both speak to the same service through
/// the same connection pools.
/// </summary>
internal sealed class ProviderHttpClientOwner(
    HttpClient providerApiClient,
    HttpClient signedTransferClient) : IDisposable
{
    public HttpClient ProviderApiClient { get; } = providerApiClient;

    public HttpClient SignedTransferClient { get; } = signedTransferClient;

    public void Dispose()
    {
        ProviderApiClient.Dispose();
        SignedTransferClient.Dispose();
    }
}
