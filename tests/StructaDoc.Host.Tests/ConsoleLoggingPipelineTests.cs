using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Web.Logging;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.System;
using StructaDoc.Host.Logging;

namespace StructaDoc.Host.Tests;

// The host registers the ServiceMantle identity (AddServiceMantle) and routes console logging
// through the locally composed Serilog pipeline (AddStructaDocConsoleLogging): a root
// LoggingLevelSwitch plus one per fixed override category, structured properties sanitized by the
// ServiceMantle core StructuredLogSanitizer before they reach the sink. These tests hold the parts
// of that contract an operator or another component can rely on: the identity the log scope and
// /api/v1/system/info share, that the default console providers really were replaced, that the
// pipeline emits the identity fields while the two override categories stay suppressed and
// secret-shaped values stay masked, and that moving a switch changes what the same process
// filters without rebuilding anything.
[Collection("Console output")]
public sealed class ConsoleLoggingPipelineTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Host_start_exposes_the_registered_service_identity()
    {
        var context = factory.Services.GetRequiredService<ServiceMantle.Web.Logging.ServiceLogContext>();

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
    public void Console_logging_runs_only_through_the_local_serilog_pipeline()
    {
        var providers = factory.Services.GetServices<ILoggerProvider>().ToArray();

        // AddStructaDocConsoleLogging removes the default MEL providers so nothing can write console
        // output around the sanitizing boundary. What must remain is the Serilog bridge.
        Assert.Single(providers);
        Assert.Equal(
            "Serilog.Extensions.Logging.SerilogLoggerProvider",
            providers[0].GetType().FullName);
    }

    // The console sink resolves Console.Out when each event is emitted, so what matters is that
    // the capture owns the console while this test logs, not when a host was built; the Console
    // output collection keeps the other console-swapping tests from stealing the writer mid-test.
    // A host built here keeps the emitted events inside this test's own window, which is what an
    // operator's console output reads like.
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
            var context = host.Services.GetRequiredService<ServiceMantle.Web.Logging.ServiceLogContext>();

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

    // The sanitizer guarantee the ServiceMantle pipeline carried: secret-shaped structured values
    // are masked before the console sees them, plain values pass, and no event can skip the pass.
    // The same core StructuredLogSanitizer does the masking here, on every property of every event.
    [Fact]
    public void Console_pipeline_masks_secret_shaped_structured_values()
    {
        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var host = CreatePipelineHost();
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("StructaDoc.SanitizerContract");

            logger.LogInformation(
                "Template {Secret} and {Plain}",
                "Bearer abcdefghijklmnopqrstuvwxyz123456",
                "plain-value");
            // A scope-published field takes the same pass: the correlation and identity fields ride
            // this path in the real host, so a secret-shaped value there is masked too.
            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["Authorization"] = "Bearer zyxwvutsrqponmlkjihg987654321",
            }))
            {
                logger.LogInformation("Context property");
            }

            logger.LogInformation(
                "Nested {@Object}",
                new { Token = "password=hunter2", Count = 3 });
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        Assert.DoesNotContain("Bearerabcdefghijklmnopqrstuvwxyz123456", output, StringComparison.Ordinal);
        Assert.DoesNotContain("zyxwvutsrqponmlkjihg987654321", output, StringComparison.Ordinal);
        Assert.DoesNotContain("password=hunter2", output, StringComparison.Ordinal);
        Assert.Contains("plain-value", output, StringComparison.Ordinal);
        Assert.Contains("Context property", output, StringComparison.Ordinal);
    }

    // The pipeline's minimum level is a switch, and a switch the running process moves is the
    // whole difference between adjusting a log level and recreating a container for it. The probe
    // host runs beside the Host's own appsettings.json, so its Logging:LogLevel section is in
    // force exactly as in the real service: the MEL pre-filter passes the shipped levels through
    // and can suppress further, but never widens them. The moves below stay inside that relation —
    // which is the relation a stored write and a deployment pin live in too.
    [Fact]
    public void Moving_a_switch_changes_what_the_same_process_logs()
    {
        var switches = new StructaDocConsoleLogSwitches();
        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        string afterRootWarning;
        string afterRootRestore;
        string afterCategoryRaise;
        string afterCategorySuppress;
        try
        {
            using var host = CreatePipelineHost(switches);
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("StructaDoc.SwitchContract");
            var efLogger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(StructaDocConsoleLogSwitches.EntityFrameworkCoreDatabaseCommandCategory);

            logger.LogInformation("Root information before any change");
            switches.TrySet(SettingCatalog.LoggingDefaultLevel, "Warning", out _);
            logger.LogInformation("Root information while Warning is in force");
            afterRootWarning = capture.Snapshot();

            switches.TrySet(SettingCatalog.LoggingDefaultLevel, "Information", out _);
            logger.LogInformation("Root information after restoring Information");
            afterRootRestore = capture.Snapshot();

            // The category switch answers independently of the root, and the honest direction for
            // it is the one the appsettings pre-filter permits: the shipped Warning lets the
            // category's warnings through, and raising the switch to Error takes them off the
            // console on the same process.
            efLogger.LogWarning("Category warning before any change");
            switches.TrySet(SettingCatalog.LoggingEfCoreDatabaseCommandLevel, "Error", out _);
            efLogger.LogWarning("Category warning while Error is in force");
            afterCategorySuppress = capture.Snapshot();

            switches.TrySet(SettingCatalog.LoggingEfCoreDatabaseCommandLevel, "Information", out _);
            efLogger.LogWarning("Category warning after restoring past the shipped level");
            efLogger.LogInformation("Category information the appsettings pre-filter holds back");
            afterCategoryRaise = capture.Snapshot();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Contains("Root information before any change", afterRootWarning, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Root information while Warning is in force",
            afterRootWarning,
            StringComparison.Ordinal);
        Assert.Contains(
            "Root information after restoring Information",
            afterRootRestore,
            StringComparison.Ordinal);
        Assert.Contains("Category warning before any change", afterCategorySuppress, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Category warning while Error is in force",
            afterCategorySuppress,
            StringComparison.Ordinal);
        // Lowering the switch back below the shipped level returns the warnings, and an
        // Information event in the category stays off the console even then: the appsettings
        // pre-filter is the layer in front, and a switch the page or the seeder moves cannot
        // reveal an event that layer holds back. Only a deployment that raises its own
        // Logging:LogLevel value widens it.
        Assert.Contains(
            "Category warning after restoring past the shipped level",
            afterCategoryRaise,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Category information the appsettings pre-filter holds back",
            afterCategoryRaise,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Seeding_reads_the_configuration_the_service_actually_uses()
    {
        var switches = new StructaDocConsoleLogSwitches();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SettingCatalog.LoggingDefaultLevel] = "warning",
                [SettingCatalog.LoggingEfCoreDatabaseCommandLevel] = "Critical",
                [SettingCatalog.LoggingAspNetCoreLevel] = "not a level",
            })
            .Build();

        switches.SeedFrom(configuration);

        // Case-insensitive parsing with the canonical spelling reported back, a level the pipeline
        // maps onto its own names, and a value no level names leaves the shipped level alone.
        Assert.True(switches.TryGet(SettingCatalog.LoggingDefaultLevel, out var root));
        Assert.Equal("Warning", root);
        Assert.True(switches.TryGet(SettingCatalog.LoggingEfCoreDatabaseCommandLevel, out var ef));
        Assert.Equal("Critical", ef);
        Assert.True(switches.TryGet(SettingCatalog.LoggingAspNetCoreLevel, out var aspNetCore));
        Assert.Equal("Warning", aspNetCore);
    }

    [Fact]
    public void Every_published_level_round_trips_through_the_switches()
    {
        var switches = new StructaDocConsoleLogSwitches();

        foreach (var key in new[]
                 {
                     SettingCatalog.LoggingDefaultLevel,
                     SettingCatalog.LoggingAspNetCoreLevel,
                     SettingCatalog.LoggingEfCoreDatabaseCommandLevel,
                 })
        {
            foreach (var level in StructaDocConsoleLogSwitches.AcceptedLevels)
            {
                Assert.True(switches.TrySet(key, level.ToLowerInvariant(), out var applied));
                Assert.Equal(level, applied);
                Assert.True(switches.TryGet(key, out var running));
                Assert.Equal(level, running);
            }
        }

        // A key the switches do not own and a value no level names are both refused, which is what
        // tells the settings write a restart is still needed rather than claiming an effect.
        Assert.False(switches.TrySet(SettingCatalog.ParseMaxConcurrency, "Warning", out _));
        Assert.False(switches.TrySet(SettingCatalog.LoggingDefaultLevel, "loud", out _));
        Assert.False(switches.TryGet(SettingCatalog.ParseMaxConcurrency, out _));
    }

    private static IHost CreatePipelineHost(StructaDocConsoleLogSwitches? switches = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        builder.Services.AddStructaDocConsoleLogging(switches ?? new StructaDocConsoleLogSwitches());
        return builder.Build();
    }
}
