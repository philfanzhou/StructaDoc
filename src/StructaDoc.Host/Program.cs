using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using ServiceMantle;
using ServiceMantle.Health;
using ServiceMantle.Web.Http;
using ServiceMantle.Web.Logging;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Conversion;
using StructaDoc.Adapters.Documents;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Providers;
using StructaDoc.Adapters.Settings;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.System;
using StructaDoc.Host.Auditing;
using StructaDoc.Host.Authentication;
using StructaDoc.Host.Documents;
using StructaDoc.Host.Health;
using StructaDoc.Host.Migrations;
using StructaDoc.Host.OpenApi;
using StructaDoc.Host.ParseRuns;
using StructaDoc.Host.Providers;
using StructaDoc.Host.Resources;
using StructaDoc.Host.Settings;
using StructaDoc.Host.Setup;
using StructaDoc.Host.Workers;

if (BusinessDatabaseMigrationCommand.TryExtractArguments(args, out var migrationArguments))
{
    Environment.ExitCode = await BusinessDatabaseMigrationCommand.ExecuteAsync(migrationArguments);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddContainerDefaults(args);

// ServiceMantle fixes the service identity every log event and /api/v1/system/info answer with:
// the stable "structadoc" service id and an instance id regenerated on every host start. No
// bootstrap file path is passed, so the bootstrap store stays a lazy singleton and this wiring
// performs no disk writes. No serviceVersion is passed: it resolves from the entry assembly
// informational version, the assembly version, then "unknown". The builder it returns is kept
// because the reverse-proxy trust registers on it below, once the options it needs are read.
// The same registration maps the health endpoints: /health/live is always 200, and /health/ready with
// its /health alias answer 200 only for the Completed + Succeeded + Reachable snapshot the
// StructaDocHealthSnapshotSource composes from the control plane and the business database.
//
// The same registration opts into the core OpenTelemetry instrumentation: ASP.NET Core and
// HttpClient tracing plus .NET runtime metrics, with no exporter registered. Trace and metric data
// stays in the process; there is no telemetry network destination of any kind by default. The
// OTel resource is exactly service.name, service.version, and service.instance.id, taken from
// the same identity the log pipeline uses.
var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("structadoc"),
        InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"))
    .AddServiceMantleHealthEndpoints()
    .AddOpenTelemetryInstrumentation();

// Console logging runs through the ServiceMantle Serilog pipeline: structured properties are
// sanitized by the library before they reach the sink, and the default MEL console providers are
// removed so no event can bypass that boundary. Registered before builder.Build() so startup
// logging, including the stored-setting fault warnings below, goes through the same pipeline.
// The two override categories keep the shipped appsettings filtering: their Information events
// stay off the console. Grafana Loki stays off; there is no remote log transport in this wiring.
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

var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
controlPlaneOptions.Validate();

// Read from the builder configuration rather than the settings-aware one below, because the key
// ring it locates is what decrypts the stored settings. Nothing under Authentication is settable
// from the browser, which is what makes reading it this early the same as reading it later; an
// architecture test holds that true.
var authenticationOptions = builder.Configuration
    .GetSection(StructaDocAuthenticationOptions.SectionName)
    .Get<StructaDocAuthenticationOptions>() ?? new StructaDocAuthenticationOptions();
authenticationOptions.Validate();

// Read from the raw configuration for the same reason, though a different one applies too: which
// peer may state what the browser asked for is a fact about the network the container was placed
// in, and an administrator reaching this service through that proxy cannot see what is in front of
// it. An architecture test holds that it stays out of the settings catalog.
var reverseProxyOptions = builder.Configuration
    .GetSection(ReverseProxyOptions.SectionName)
    .Get<ReverseProxyOptions>() ?? new ReverseProxyOptions();
reverseProxyOptions.Validate();
// The trust registration is validated with the host, not at composition: ServiceMantle parses the
// named peers, the published hosts, and the forward limit into one snapshot when the host starts,
// so an unusable value fails startup rather than becoming a proxy that silently does nothing.
serviceMantle.AddStructaDocForwardedHeaders(reverseProxyOptions);

// The key ring has to exist before the stored settings below are decrypted. In the database form
// it lives in the business database itself, which only works when that database's location does
// not depend on the ring: the connection string must be pinned by the deployment. A stored
// Database section together with the database key ring is that cycle, so it fails here with a
// stable error rather than surfacing later as a decryption failure nobody can place.
IDataProtectionProvider keyRing;
if (authenticationOptions.DataProtectionKeyPersistence == DataProtectionKeyPersistence.Database)
{
    if (StructaDocSettingsConfiguration.HasStoredSection(
            controlPlaneOptions,
            SettingCatalog.DatabaseSection,
            args))
    {
        throw new InvalidOperationException(
            "Authentication:DataProtectionKeyPersistence is Database, but the Database section carries a stored value. "
                + "Pin the business database (Database__*) on every container before enabling the database key ring: "
                + "a connection string the key ring has to decrypt cannot be what locates the key ring.");
    }

    // Nothing is stored in the Database section here, so the deployment configuration alone
    // decides where the business database is, and the options the key ring binds are the options
    // the rest of the startup binds later.
    var pinnedDatabaseOptions = builder.Configuration
        .GetSection(DatabaseOptions.SectionName)
        .Get<DatabaseOptions>() ?? new DatabaseOptions();
    pinnedDatabaseOptions.Validate();
    keyRing = StructaDocKeyRing.CreateDatabase(authenticationOptions, pinnedDatabaseOptions);
}
else
{
    keyRing = StructaDocKeyRing.Create(authenticationOptions);
}

var settingSecretProtector = new DataProtectionSettingSecretProtector(keyRing);
var settingsStartupFault = new SettingsStartupFault();

// Settings an administrator chose in the browser join configuration before anything is read from
// it. Everything below binds against this rather than the raw builder configuration, or a stored
// setting would be visible to the administration page and invisible to the service using it.
var settingsConfiguration = StructaDocSettingsConfiguration.Create(
    builder.Configuration,
    controlPlaneOptions,
    args,
    settingSecretProtector,
    settingsStartupFault);
var configuration = settingsConfiguration.Effective;

// Where business data and documents live are the two settings whose wrong value leaves nothing
// working, and both are reachable from the browser, so both are bound as recoverable sections: a
// stored value the service cannot use is dropped and reported rather than allowed to stop startup.
// What survives is the control plane, which is what the administration area runs on.
var databaseOptions = RecoverableConfigurationBinder.Bind(
    settingsConfiguration,
    settingsStartupFault,
    SettingCatalog.DatabaseSection,
    "business-database configuration",
    source => source.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
        ?? new DatabaseOptions(),
    options => options.Validate());
var workerOptions = configuration
    .GetSection(ParseRunWorkerOptions.SectionName)
    .Get<ParseRunWorkerOptions>() ?? new ParseRunWorkerOptions();
workerOptions.Validate();
var ingestionOptions = configuration
    .GetSection(DocumentIngestionOptions.SectionName)
    .Get<DocumentIngestionOptions>() ?? new DocumentIngestionOptions();
var storageOptions = RecoverableConfigurationBinder.Bind(
    settingsConfiguration,
    settingsStartupFault,
    SettingCatalog.StorageSection,
    "storage configuration",
    source => source.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>()
        ?? new FileStorageOptions(),
    options => options.Validate());
var providerResultOptions = configuration
    .GetSection(ProviderResultIntakeOptions.SectionName)
    .Get<ProviderResultIntakeOptions>() ?? new ProviderResultIntakeOptions();
var providerResultNormalizationOptions = configuration
    .GetSection(ProviderResultNormalizationOptions.SectionName)
    .Get<ProviderResultNormalizationOptions>() ?? new ProviderResultNormalizationOptions();
var conversionOptions = configuration
    .GetSection(LibreOfficeConversionOptions.SectionName)
    .Get<LibreOfficeConversionOptions>() ?? new LibreOfficeConversionOptions();
ingestionOptions.Validate();
providerResultOptions.Validate();
providerResultNormalizationOptions.Validate();
conversionOptions.Validate();
var oidcOptions = OidcConfigurationBinder.Bind(settingsConfiguration, settingsStartupFault);

builder.Services.AddStructaDocControlPlane(controlPlaneOptions);
builder.Services.AddStructaDocPersistence(databaseOptions);
// Readiness is served by the ServiceMantle health endpoints, which read one snapshot per request
// from the source below. The registration is scoped because the probes run on the request's own
// control-plane and business-database contexts.
builder.Services.AddScoped<IServiceHealthSnapshotSource, StructaDocHealthSnapshotSource>();
builder.Services.AddStructaDocDocumentIngestion(ingestionOptions, storageOptions);
builder.Services.AddStructaDocDocumentConversion(conversionOptions);
builder.Services.AddStructaDocParseProviders();
builder.Services.AddStructaDocProviderResults(
    providerResultOptions,
    providerResultNormalizationOptions);
builder.Services.AddStructaDocHostAuthentication(authenticationOptions, oidcOptions, keyRing, serviceMantle);
builder.Services.AddStructaDocApiDescription();
builder.Services.AddSingleton(oidcOptions);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(settingsConfiguration);
builder.Services.AddSingleton(settingsStartupFault);
builder.Services.AddSingleton<ISettingSecretProtector>(settingSecretProtector);
builder.Services.AddSingleton<OidcDiscoveryProbe>();
builder.Services.AddSingleton<StorageConnectionProbe>();
builder.Services.AddSingleton<DatabaseConnectionProbe>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ParseRunLeaseHeartbeat>();
builder.Services.AddScoped<ParseRunExecutor>();
builder.Services.AddScoped<LargePdfParseOrchestrator>();
builder.Services.AddScoped<StructaDoc.Application.ParseRuns.IParseExportService, ParseExportService>();
builder.Services.AddHostedService<ParseRunMaintenanceWorker>();
builder.Services.AddHostedService<ParseRunExecutionWorker>();
builder.Services.AddHostedService<ResourceCleanupWorker>();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = checked(ingestionOptions.MaxUploadBytes + (1024 * 1024)));

var app = builder.Build();

// Dropping a stored setting is decided before logging exists, so it is reported here. An operator
// reading the container log is the one person who would otherwise see a service that started
// cleanly with a feature quietly switched off.
foreach (var (section, detail) in settingsStartupFault.Faults)
{
    app.Logger.LogWarning(
        "Stored configuration in section {Section} was not applied. {Detail}",
        section,
        detail);
}

// The control plane is migrated first and unconditionally: administration must be reachable even
// when the configured business database is not, and it is what an administrator uses to fix that.
await app.Services.ApplyStructaDocControlPlaneMigrationsAsync(app.Lifetime.ApplicationStopping);
// A business database an administrator pointed at from the browser can be absent, refuse the
// credentials, or be a server this build cannot migrate, and none of that is visible until the
// service starts. Stopping here would take away the administration area, which is the only place it
// can be corrected from, so a stored configuration that cannot be prepared is recorded and the
// service starts without a usable business database. Readiness still fails, so nothing routes real
// traffic to it, and a database the deployment pinned still stops startup as before.
// The migration itself runs under the ServiceMantle orchestration: the InnoDB preflight, the legacy
// administrator import, and the assembly migrations run as one workflow under a provider lease, so
// two instances starting against the same server database cannot apply it twice.
try
{
    if (databaseOptions.ApplyMigrationsOnStartup)
    {
        await app.Services.ApplyStructaDocMigrationsAsync(
            databaseOptions,
            app.Lifetime.ApplicationStopping);
    }
}
catch (Exception error) when (
    error is not OperationCanceledException
    && settingsConfiguration.IsStoredSection(SettingCatalog.DatabaseSection))
{
    settingsStartupFault.Record(
        SettingCatalog.DatabaseSection,
        "The configured business database could not be prepared, so documents and parsing are unavailable. "
            + DatabaseConnectionProbe.SanitizeMessage(error, databaseOptions.ConnectionString));
    app.Logger.LogError(
        error,
        "The configured business database could not be prepared. The administration area remains available so the configuration can be corrected.");
}
await app.Services.BootstrapStructaDocAdministratorAsync(
    authenticationOptions,
    app.Lifetime.ApplicationStopping);

// A deployment with no Provider can accept documents and parse none of them, and assembling the
// first one from documentation is the step a first-run administrator has no way to get right. The
// official endpoint is therefore already configured, missing only its token. It needs the business
// database, which the block above is allowed to leave unusable, so a failure here is recorded and
// startup continues: the administration area is where both would be corrected.
try
{
    await app.Services.SeedStructaDocOfficialProviderAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception error) when (error is not OperationCanceledException)
{
    app.Logger.LogError(
        error,
        "The official Provider could not be configured. An administrator can create one under /admin.");
}

// First, because everything after it reads a scheme, a host, or a caller's address that is only
// correct once the proxy in front has been believed: cookies decide `Secure` from the scheme, the
// sign-in redirect address composed for an identity provider is built from the scheme and host, and
// the rate limiter below partitions on the caller's address.
app.UseStructaDocReverseProxy(reverseProxyOptions, app.Logger);

// Every response answers with the request's Correlation ID in `x-correlation-id`, including 4xx/5xx
// from anything below, so a report from a consumer can be matched to the exact request in the logs.
// It sits after the proxy trust (the proxy decision is not correlated) and before every other
// downstream component, and the same value enters the downstream ILogger scope beside the identity
// fields, so events written while handling a request carry the ID that was returned to the caller.
app.UseServiceMantleCorrelationId();

// Uncaught exceptions from anything below — endpoints, authentication, the works — are answered with
// one environment-independent RFC 7807 body instead of an empty 500 or a dropped connection: the
// fixed `type`, `title`, `status`, `correlationId`, and `errorCode` fields, and nothing else. The
// middleware never inspects or writes the exception message, stack, inner exceptions, or `Data`, so
// diagnostics live in the logs linked by that correlation ID. It sits inside the correlation
// middleware so the fallback body carries the same value the response header and log scope do, and
// outside everything else so the whole downstream surface is covered. Responses an endpoint produces
// itself — every existing Results.Problem path — never reach this handler. Development keeps the
// same safe body: there is no development-details switch, so local diagnostics start from the log.
app.UseServiceMantleProblemDetails();

// Setup and administration endpoints are marked with `RequireServiceMantleSecurityResponseHeaders`,
// and this middleware is what turns that mark into the six-header baseline (`Cache-Control:
// no-store`, `Pragma: no-cache`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
// `Referrer-Policy: no-referrer`, and a `default-src 'none'` CSP) on everything they answer:
// success, validation failure, 401/403, the 429 from the rate limiter below, and unhandled
// exceptions. It sits after routing — the endpoint and its metadata are what decide — and before
// every component below that can produce one of those responses. The unhandled-exception case is
// answered one middleware up, by the Problem Details fallback writing its 500 on this very response,
// and the headers ride an on-starting callback registered here on that response, so they are on it
// when it starts: the baseline holds although the fallback body is produced outside this middleware
// (held by Unhandled_exception_fallback_of_marked_endpoints_carries_the_baseline). Unmarked routes,
// the SPA, and static content keep exactly the headers they had.
app.UseServiceMantleSecurityResponseHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();
// Before authentication because the page carries no credential of its own, and before the endpoint
// that answers unmatched routes with the application shell, which would otherwise return HTML for
// every path under it.
app.UseStructaDocApiDescriptionPage();
app.UseAuthentication();
// After authentication and before authorization, because the `servicemantle.management` policy
// partitions an authenticated caller by its management identity and falls back to the caller's
// address otherwise; the resolver needs the principal that authentication has just produced. The
// sign-in endpoint itself is anonymous, so its partition is the address either way.
app.UseRateLimiter();
app.UseAuthorization();

// The workspace and administration areas are client-side routes of one SPA, so the Host answers
// unmatched navigation paths with the application shell. Service paths must be excluded, or a
// mistyped route answers 200 with HTML instead of failing as an API call. This rejects the
// selected fallback rather than mapping a competing endpoint, so a path that exists under
// another HTTP method still resolves to 405 instead of 404.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<ClientRouteFallbackMarker>() is not null
        && (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/health")))
    {
        await Results
            .Problem(statusCode: StatusCodes.Status404NotFound, title: "Endpoint not found")
            .ExecuteAsync(context);
        return;
    }

    await next(context);
});

var serviceLogContext = app.Services.GetRequiredService<ServiceLogContext>();

app.MapStructaDocApiDescription();

app.MapGet(
        "/api/v1/system/info",
        () => new ServiceInfoResponse("StructaDoc", serviceLogContext.ServiceVersion))
    .WithName("GetServiceInfo");

if (ingestionOptions.UploadApiEnabled)
{
    app.MapDocumentUpload(ingestionOptions.MaxUploadBytes);
}
app.MapDocumentReadEndpoints();
app.MapDocumentAccessGrantEndpoints();

app.MapSetupEndpoints(authenticationOptions.AdministratorSessionLifetime);
app.MapAdministratorSessionEndpoints(
    authenticationOptions.AdministratorSessionLifetime);
app.MapInteractiveSessionEndpoints(oidcOptions);
app.MapAdministratorAccountEndpoints(
    authenticationOptions.AdministratorSessionLifetime);
app.MapManagementAuditQueryEndpoints();
app.MapApiClientAdministrationEndpoints();
app.MapSettingsEndpoints();
app.MapOidcSettingsEndpoints();
app.MapInfrastructureSettingsEndpoints();
app.MapSystemControlEndpoints();
app.MapProviderConfigAdministrationEndpoints();
app.MapParseRunEndpoints();
app.MapParseResultEndpoints();
app.MapParseExportEndpoints();
app.MapResourceDeletionEndpoints();

app.MapServiceMantleHealthEndpoints();

app.MapFallbackToFile("index.html")
    .WithMetadata(new ClientRouteFallbackMarker());

app.Run();

internal sealed class ClientRouteFallbackMarker;

public partial class Program;
