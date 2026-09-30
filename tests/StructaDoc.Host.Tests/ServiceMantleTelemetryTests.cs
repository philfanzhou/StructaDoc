using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle;
using ServiceMantle.Diagnostics.Instrumentation;

namespace StructaDoc.Host.Tests;

// The Host opts into the ServiceMantle core OpenTelemetry instrumentation
// (AddOpenTelemetryInstrumentation, default options). These tests hold the observable contract:
// both providers are registered and resolved by the running host, the OTel resource is exactly the
// three identity fields the log pipeline uses, no export-side registration of any kind exists, and
// a registration that enables nothing fails host startup with a stable error.
public sealed class ServiceMantleTelemetryTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public void Host_startup_registers_and_resolves_both_telemetry_providers()
    {
        // The hosted app runs with the default-options registration, so resolving both providers
        // is what "the host starts with instrumentation active" means in DI terms.
        Assert.NotNull(factory.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(factory.Services.GetRequiredService<MeterProvider>());
    }

    [Fact]
    public void Telemetry_resource_is_exactly_the_three_identity_fields()
    {
        var context = factory.Services.GetRequiredService<ServiceMantle.Web.Logging.ServiceLogContext>();
        var tracer = factory.Services.GetRequiredService<TracerProvider>();
        var meter = factory.Services.GetRequiredService<MeterProvider>();

        foreach (var provider in new object[] { tracer, meter })
        {
            var attributes = ReadResourceAttributes(provider);

            // Exactly three attributes, taken from the identity the log pipeline and
            // /api/v1/system/info answers use, so telemetry, logs, and the info endpoint cannot
            // disagree about which service produced them.
            Assert.Equal(
                new Dictionary<string, object>
                {
                    ["service.name"] = context.ServiceName,
                    ["service.version"] = context.ServiceVersion,
                    ["service.instance.id"] = context.InstanceId,
                },
                attributes);
        }
    }

    // "No exporter" is a registration-level property: nothing on the export side — ServiceMantle's
    // OTLP and Prometheus capabilities, or an OpenTelemetry SDK exporter type — is registered, so
    // no trace or metric data can leave the process from this wiring. The host's registrations are
    // captured through a derived factory: its ConfigureServices callbacks run after Program.cs's
    // registrations, exactly like the shared factory's own do, so the captured list is the complete
    // registration set the real host builds from.
    [Fact]
    public void Default_registration_has_no_telemetry_export_side()
    {
        var descriptors = new List<ServiceDescriptor>();
        using var scanFactory = factory.WithWebHostBuilder(
            builder => builder.ConfigureServices(services => descriptors.AddRange(services)));

        using (scanFactory.CreateClient())
        {
            Assert.NotEmpty(descriptors);
        }

        var exportRegistrations = descriptors.Where(descriptor =>
                IsExportType(descriptor.ServiceType)
                || IsExportType(descriptor.ImplementationType)
                || IsExportType(descriptor.ImplementationInstance?.GetType()))
            .ToArray();

        Assert.Empty(exportRegistrations);
    }

    [Fact]
    public async Task Enabled_registration_with_no_instrumentation_fails_host_startup()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddServiceMantle(
                ServiceId.Parse("structadoc"),
                InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"))
            .AddOpenTelemetryInstrumentation(options =>
            {
                // Enabled with every selection off selects nothing to instrument, which the
                // registration validates when the host starts, before any provider activates.
                options.Enabled = true;
                options.EnableAspNetCoreTracing = false;
                options.EnableHttpClientTracing = false;
                options.EnableRuntimeMetrics = false;
            });
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Enabled OpenTelemetry instrumentation must include at least one instrumentation.",
            error.Message);
    }

    // The registration is opt-in: a host that does not call it has neither provider, so no
    // component accidentally depends on telemetry being present.
    [Fact]
    public void Host_without_the_registration_has_no_telemetry_providers()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        using var host = builder.Build();

        Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<TracerProvider>());
        Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<MeterProvider>());
    }

    private static bool IsExportType(Type? type) =>
        type?.FullName is { } fullName
        && (fullName.StartsWith("ServiceMantle.Diagnostics.Export.", StringComparison.Ordinal)
            || fullName.StartsWith("OpenTelemetry.Exporter", StringComparison.Ordinal));

    /// <summary>
    /// The providers' concrete SDK types are internal, so the resource they were built with is
    /// read through reflection. The resource itself is public and immutable; this reads exactly
    /// the attributes the registration placed there.
    /// </summary>
    private static IReadOnlyDictionary<string, object> ReadResourceAttributes(object provider)
    {
        var resource = provider.GetType()
            .GetProperty("Resource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(provider)
            ?? throw new InvalidOperationException("The provider exposes no Resource.");
        var attributes = resource.GetType()
            .GetProperty("Attributes")
            ?.GetValue(resource) as IEnumerable<KeyValuePair<string, object>>
            ?? throw new InvalidOperationException("The resource exposes no Attributes.");

        return attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
