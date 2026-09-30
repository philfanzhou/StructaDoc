using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Web.Logging;
using StructaDoc.Contracts.System;

namespace StructaDoc.Host.Tests;

// The host registers the ServiceMantle identity (AddServiceMantle) and routes console logging
// through the ServiceMantle Serilog pipeline (AddServiceMantleSerilog). These tests hold the parts
// of that contract an operator or another component can rely on: the identity the log scope and
// /api/v1/system/info share, that the default console providers really were replaced, that the
// pipeline emits the identity fields while the two override categories stay suppressed, and that a
// conflicting Serilog configuration fails host startup with a stable error code.
public sealed class ServiceMantleLoggingTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Host_start_exposes_the_registered_service_identity()
    {
        var context = factory.Services.GetRequiredService<ServiceLogContext>();

        Assert.Equal("structadoc", context.ServiceName);
        Assert.False(string.IsNullOrWhiteSpace(context.ServiceVersion));
        Assert.StartsWith("structadoc-", context.InstanceId, StringComparison.Ordinal);

        using var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<ServiceInfoResponse>(
            "/api/v1/system/info",
            cancellationToken: TestContext.Current.CancellationToken);

        // /api/v1/system/info keeps its published shape and now answers from the same identity
        // context the log pipeline uses, so the two can never disagree.
        Assert.NotNull(payload);
        Assert.Equal("StructaDoc", payload.Name);
        Assert.Equal(context.ServiceVersion, payload.Version);
    }

    [Fact]
    public void Console_logging_runs_only_through_the_service_mantle_pipeline()
    {
        var providers = factory.Services.GetServices<ILoggerProvider>().ToArray();

        // AddServiceMantleSerilog removes the default MEL providers so nothing can write console
        // output around the sanitizing boundary. What must remain is the pipeline's own provider.
        Assert.Single(providers);
        Assert.Equal(
            "ServiceMantle.Logging.Pipeline.RuntimeLoggerProvider",
            providers[0].GetType().FullName);
    }

    // The shared web hosts start whenever their test class does, so a console sink one of them
    // created may already be bound to the original output stream. A host built here, after the
    // stream was swapped, is the way to observe this pipeline's actual console output, which is
    // what an operator reads.
    [Fact]
    public void Console_pipeline_emits_identity_fields_and_suppresses_override_categories()
    {
        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var host = CreatePipelineHost();
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var context = host.Services.GetRequiredService<ServiceLogContext>();

            var applicationLogger = loggerFactory.CreateLogger("StructaDoc.PipelineContract");
            using (context.BeginScope(applicationLogger))
            {
                applicationLogger.LogInformation("Identity scope contract message");
            }

            loggerFactory
                .CreateLogger("Microsoft.AspNetCore.Hosting")
                .LogInformation("Suppressed AspNetCore information");
            loggerFactory
                .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                .LogInformation("Suppressed EntityFrameworkCore information");
            loggerFactory
                .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                .LogWarning("Emitted EntityFrameworkCore warning");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        Assert.Contains("Identity scope contract message", output, StringComparison.Ordinal);
        Assert.Contains("ServiceName", output, StringComparison.Ordinal);
        Assert.Contains("structadoc", output, StringComparison.Ordinal);
        Assert.Contains("ServiceVersion", output, StringComparison.Ordinal);
        Assert.Contains("InstanceId", output, StringComparison.Ordinal);
        Assert.Contains("structadoc-", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Suppressed AspNetCore information", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Suppressed EntityFrameworkCore information",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Emitted EntityFrameworkCore warning", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_serilog_configuration_fails_host_startup_with_a_stable_error_code()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        // A Serilog logger already registered in DI is the conflicting configuration the pipeline
        // must fail closed on: it could feed events around the sanitizing boundary.
        builder.Services.AddSingleton<Serilog.ILogger>(
            new Serilog.LoggerConfiguration().CreateLogger());
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
                ["Microsoft.EntityFrameworkCore.Database.Command"] = LogLevel.Warning,
            };
            options.IncludeScopes = true;
        });
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<SerilogConfigurationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("serilog.console_sink_conflict", error.ErrorCode);
    }

    private static IHost CreatePipelineHost()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
                ["Microsoft.EntityFrameworkCore.Database.Command"] = LogLevel.Warning,
            };
            options.IncludeScopes = true;
        });
        return builder.Build();
    }
}
