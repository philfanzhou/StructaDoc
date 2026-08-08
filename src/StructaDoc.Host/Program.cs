using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Contracts.System;
using StructaDoc.Host.Authentication;
using StructaDoc.Host.Documents;
using StructaDoc.Host.ParseRuns;
using StructaDoc.Host.Providers;
using StructaDoc.Host.Resources;
using StructaDoc.Host.Setup;
using StructaDoc.Host.Workers;
using StructaDoc.Infrastructure.Authentication;
using StructaDoc.Infrastructure.ControlPlane;
using StructaDoc.Infrastructure.Conversion;
using StructaDoc.Infrastructure.Documents;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.ProviderResults;
using StructaDoc.Infrastructure.Providers;
using StructaDoc.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
var workerOptions = builder.Configuration
    .GetSection(ParseRunWorkerOptions.SectionName)
    .Get<ParseRunWorkerOptions>() ?? new ParseRunWorkerOptions();
workerOptions.Validate();
var ingestionOptions = builder.Configuration
    .GetSection(DocumentIngestionOptions.SectionName)
    .Get<DocumentIngestionOptions>() ?? new DocumentIngestionOptions();
var storageOptions = builder.Configuration
    .GetSection(FileStorageOptions.SectionName)
    .Get<FileStorageOptions>() ?? new FileStorageOptions();
var providerResultOptions = builder.Configuration
    .GetSection(ProviderResultIntakeOptions.SectionName)
    .Get<ProviderResultIntakeOptions>() ?? new ProviderResultIntakeOptions();
var providerResultNormalizationOptions = builder.Configuration
    .GetSection(ProviderResultNormalizationOptions.SectionName)
    .Get<ProviderResultNormalizationOptions>() ?? new ProviderResultNormalizationOptions();
var conversionOptions = builder.Configuration
    .GetSection(LibreOfficeConversionOptions.SectionName)
    .Get<LibreOfficeConversionOptions>() ?? new LibreOfficeConversionOptions();
ingestionOptions.Validate();
storageOptions.Validate();
providerResultOptions.Validate();
providerResultNormalizationOptions.Validate();
conversionOptions.Validate();
var authenticationOptions = builder.Configuration
    .GetSection(StructaDocAuthenticationOptions.SectionName)
    .Get<StructaDocAuthenticationOptions>() ?? new StructaDocAuthenticationOptions();
authenticationOptions.Validate();
var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
controlPlaneOptions.Validate();
var oidcOptions = builder.Configuration
    .GetSection(OidcAuthenticationOptions.SectionName)
    .Get<OidcAuthenticationOptions>() ?? new OidcAuthenticationOptions();
oidcOptions.Validate();

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
builder.Services.AddStructaDocHostAuthentication(authenticationOptions, oidcOptions);
builder.Services.AddSingleton(oidcOptions);
builder.Services.AddSingleton(workerOptions);
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

// The control plane is migrated first and unconditionally: administration must be reachable even
// when the configured business database is not, and it is what an administrator uses to fix that.
await app.Services.ApplyStructaDocControlPlaneMigrationsAsync(app.Lifetime.ApplicationStopping);
await app.Services.ApplyStructaDocMigrationsAsync(
    databaseOptions,
    app.Lifetime.ApplicationStopping);
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
