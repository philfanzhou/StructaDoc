using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.System;
using StructaDoc.Host.Authentication;
using StructaDoc.Host.Documents;
using StructaDoc.Host.ParseRuns;
using StructaDoc.Host.Providers;
using StructaDoc.Host.Resources;
using StructaDoc.Host.Settings;
using StructaDoc.Host.Setup;
using StructaDoc.Host.Workers;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Conversion;
using StructaDoc.Adapters.Documents;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Providers;
using StructaDoc.Adapters.Settings;
using StructaDoc.Adapters.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddContainerDefaults(args);

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

var keyRing = StructaDocKeyRing.Create(authenticationOptions);
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

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
builder.Services.AddStructaDocControlPlane(controlPlaneOptions);
builder.Services.AddStructaDocPersistence(databaseOptions);
builder.Services.AddStructaDocDocumentIngestion(ingestionOptions, storageOptions);
builder.Services.AddStructaDocDocumentConversion(conversionOptions);
builder.Services.AddStructaDocParseProviders();
builder.Services.AddStructaDocProviderResults(
    providerResultOptions,
    providerResultNormalizationOptions);
builder.Services.AddStructaDocHostAuthentication(authenticationOptions, oidcOptions, keyRing);
builder.Services.AddSingleton(oidcOptions);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(settingsConfiguration);
builder.Services.AddSingleton(settingsStartupFault);
builder.Services.AddSingleton<ISettingSecretProtector>(settingSecretProtector);
builder.Services.AddSingleton<OidcDiscoveryProbe>();
builder.Services.AddSingleton<StorageConnectionProbe>();
builder.Services.AddSingleton<DatabaseConnectionProbe>();
builder.Services.AddSingleton(new ParseExecutionGate(workerOptions.ExecutionEnabled));
builder.Services.AddSingleton<ISettingChangeListener>(
    services => services.GetRequiredService<ParseExecutionGate>());
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
try
{
    await app.Services.ApplyStructaDocMigrationsAsync(
        databaseOptions,
        app.Lifetime.ApplicationStopping);
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

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
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

var serviceVersion = Assembly
    .GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";

app.MapGet(
        "/api/v1/system/info",
        () => new ServiceInfoResponse("StructaDoc", serviceVersion))
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

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
    });

app.MapHealthChecks("/health/ready");

app.MapFallbackToFile("index.html")
    .WithMetadata(new ClientRouteFallbackMarker());

app.Run();

internal sealed class ClientRouteFallbackMarker;

public partial class Program;
