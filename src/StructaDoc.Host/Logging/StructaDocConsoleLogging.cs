using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ServiceMantle.Logging;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Logging;

/// <summary>
/// The level switches of the locally composed console pipeline: one for the pipeline's root
/// minimum and one for each of the two fixed override categories. Every switch is created at the
/// level the shipped <c>appsettings.json</c> describes, re-seeded once from the settings-aware
/// configuration before the host starts, and handed to <see cref="ISettingChangeListener"/> flips
/// from then on — which is what makes the log level adjustable at runtime rather than only at the
/// next start.
/// </summary>
public sealed class StructaDocConsoleLogSwitches
{
    public const string AspNetCoreCategory = "Microsoft.AspNetCore";
    public const string EntityFrameworkCoreDatabaseCommandCategory =
        "Microsoft.EntityFrameworkCore.Database.Command";

    /// <summary>
    /// The level names these keys accept, in the spelling Microsoft.Extensions.Logging
    /// configuration has always used for the <c>Logging:LogLevel</c> section. The Serilog pipeline
    /// maps them onto its own names (<c>Trace</c> to Verbose, <c>Critical</c> to Fatal) internally,
    /// so a stored value, a pinned value, and an <c>appsettings.json</c> value all read the same.
    /// </summary>
    public static readonly IReadOnlyList<string> AcceptedLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private static readonly (string Spelling, LogEventLevel Level)[] LevelMap =
    [
        ("Trace", LogEventLevel.Verbose),
        ("Debug", LogEventLevel.Debug),
        ("Information", LogEventLevel.Information),
        ("Warning", LogEventLevel.Warning),
        ("Error", LogEventLevel.Error),
        ("Critical", LogEventLevel.Fatal),
    ];

    internal LoggingLevelSwitch Root { get; } = new(LogEventLevel.Information);

    internal LoggingLevelSwitch AspNetCore { get; } = new(LogEventLevel.Warning);

    internal LoggingLevelSwitch EntityFrameworkCore { get; } = new(LogEventLevel.Warning);

    /// <summary>
    /// Sets the switch behind <paramref name="key"/> when the key is one of the three this class
    /// owns and <paramref name="level"/> names a level <see cref="AcceptedLevels"/> accepts. The
    /// canonical spelling of the level that was applied is returned in
    /// <paramref name="appliedLevel"/>, so a caller logging or reporting the change says what the
    /// pipeline now filters at rather than what was typed.
    /// </summary>
    public bool TrySet(string key, string? level, out string? appliedLevel)
    {
        appliedLevel = null;
        if (SwitchFor(key) is not { } switches
            || !TryParseLevel(level, out var spelling, out var parsed))
        {
            return false;
        }

        switches.MinimumLevel = parsed;
        appliedLevel = spelling;
        return true;
    }

    /// <summary>
    /// True when <paramref name="key"/> is one of the three this class owns; the level the running
    /// pipeline filters that key at is returned in <paramref name="level"/>, in the spelling the
    /// settings catalog publishes.
    /// </summary>
    public bool TryGet(string key, out string? level)
    {
        if (SwitchFor(key) is { } switches)
        {
            level = SpellingFor(switches.MinimumLevel);
            return true;
        }

        level = null;
        return false;
    }

    /// <summary>
    /// Seeds every switch from the configuration the service actually reads: a deployment pin, a
    /// stored value, or the shipped <c>appsettings.json</c> value, in the usual precedence. A value
    /// that names no accepted level leaves the switch where it was — the shipped level — because a
    /// stored row the write-time validation would refuse is not one to steer the console with.
    /// </summary>
    public void SeedFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var key in Keys())
        {
            if (SwitchFor(key) is { } switches
                && TryParseLevel(configuration[key], out _, out var parsed))
            {
                switches.MinimumLevel = parsed;
            }
        }
    }

    private LoggingLevelSwitch? SwitchFor(string? key)
    {
        return key switch
        {
            SettingCatalog.LoggingDefaultLevel => Root,
            SettingCatalog.LoggingAspNetCoreLevel => AspNetCore,
            SettingCatalog.LoggingEfCoreDatabaseCommandLevel => EntityFrameworkCore,
            _ => null,
        };
    }

    private static IEnumerable<string> Keys()
    {
        yield return SettingCatalog.LoggingDefaultLevel;
        yield return SettingCatalog.LoggingAspNetCoreLevel;
        yield return SettingCatalog.LoggingEfCoreDatabaseCommandLevel;
    }

    private static bool TryParseLevel(string? value, out string spelling, out LogEventLevel level)
    {
        spelling = string.Empty;
        level = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var match = LevelMap.FirstOrDefault(
            candidate => string.Equals(candidate.Spelling, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match.Spelling is null)
        {
            return false;
        }

        spelling = match.Spelling;
        level = match.Level;
        return true;
    }

    private static string SpellingFor(LogEventLevel level)
    {
        return LevelMap.Single(candidate => candidate.Level == level).Spelling;
    }
}

/// <summary>
/// Rewrites every structured property of every event through the ServiceMantle core
/// <see cref="StructuredLogSanitizer"/> before the event reaches a sink, so secret-shaped values
/// are masked rather than written to the console no matter which component produced them. It is
/// registered after <c>Enrich.FromLogContext()</c>, which is what puts the scope-published fields
/// on the event, so those are sanitized on the same pass as template-bound properties. Property
/// names pass through: the sanitizer masks values, and the field-name rules it carries are name
/// filters the default options leave open.
/// </summary>
internal sealed class SanitizingLogEventEnricher(StructuredLogSanitizer sanitizer) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // A snapshot first: AddOrUpdateProperty mutates the dictionary being iterated.
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var rewritten = Rewrite(value);
            if (!ReferenceEquals(rewritten, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, rewritten));
            }
        }
    }

    private LogEventPropertyValue Rewrite(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                var sanitized = sanitizer.Sanitize(scalar.Value);
                return ReferenceEquals(sanitized, scalar.Value) || Equals(sanitized, scalar.Value)
                    ? scalar
                    : Plain(sanitized);

            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(Rewrite));

            case DictionaryValue dictionary:
                return new DictionaryValue(
                    dictionary.Elements.Select(pair =>
                        new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, Rewrite(pair.Value))));

            case StructureValue structure:
                return new StructureValue(
                    structure.Properties.Select(property =>
                        new LogEventProperty(property.Name, Rewrite(property.Value))),
                    structure.TypeTag);

            default:
                return value;
        }
    }

    /// <summary>
    /// The plain object the sanitizer returned for a scalar: normally a masked string, and for
    /// anything unexpected a value the console renders through its own formatting rather than one
    /// that is dropped.
    /// </summary>
    private static LogEventPropertyValue Plain(object? value)
    {
        return value switch
        {
            null => ScalarValue.Null,
            string text => new ScalarValue(text),
            LogEventPropertyValue alreadyBound => alreadyBound,
            IReadOnlyDictionary<string, object?> mapped => new DictionaryValue(
                mapped.Select(pair =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        new ScalarValue(pair.Key),
                        Plain(pair.Value)))),
            _ => new ScalarValue(value),
        };
    }
}

public static class StructaDocConsoleLoggingServiceCollectionExtensions
{
    private const string ConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Composes the console pipeline in this repository rather than through
    /// ServiceMantle.Logging's Serilog host, because the level has to move at runtime and the
    /// published ServiceMantle surface takes its levels once at startup. What the local
    /// composition keeps is the boundary the library pipeline existed for: structured properties
    /// are sanitized by the same core <see cref="StructuredLogSanitizer"/> before they reach the
    /// sink, and the default Microsoft.Extensions.Logging console providers are removed so no
    /// event can reach the console around it. The <c>Logging:LogLevel</c> configuration section
    /// keeps its role in front of the pipeline: it still pre-filters what reaches the Serilog
    /// provider — it can suppress events further, but cannot reveal an event the pipeline's own
    /// switches hold back.
    /// </summary>
    public static IServiceCollection AddStructaDocConsoleLogging(
        this IServiceCollection services,
        StructaDocConsoleLogSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(switches);

        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(switches.Root)
            .MinimumLevel.Override(
                StructaDocConsoleLogSwitches.AspNetCoreCategory,
                switches.AspNetCore)
            .MinimumLevel.Override(
                StructaDocConsoleLogSwitches.EntityFrameworkCoreDatabaseCommandCategory,
                switches.EntityFrameworkCore)
            .Enrich.FromLogContext()
            .Enrich.With(new SanitizingLogEventEnricher(
                new StructuredLogSanitizer(new StructuredLogSanitizerOptions())))
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .CreateLogger();

        // Removing the provider registrations the host builder added — console, debug, event
        // source — is what leaves the Serilog bridge as the only path to the console. The default
        // logger factory stays, so the Logging:LogLevel configuration rules still apply in front
        // of the pipeline exactly as they did with the library's provider.
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(ILoggerProvider))
            {
                services.RemoveAt(index);
            }
        }

        services.AddSingleton(switches);
        // The concrete logger is registered so the container disposes it with the host; the
        // provider does not own it, and a shared registration means one pipeline per process.
        services.AddSingleton<Serilog.ILogger>(logger);
        services.AddSingleton<ILoggerProvider>(
            serviceProvider => new SerilogLoggerProvider(
                serviceProvider.GetRequiredService<Serilog.ILogger>(),
                dispose: false));
        return services;
    }
}
