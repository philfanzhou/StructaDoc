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
using StructaDoc.Host.Workers;
using StructaDoc.Infrastructure.Authentication;
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
ingestionOptions.Validate();
storageOptions.Validate();
providerResultOptions.Validate();
providerResultNormalizationOptions.Validate();
var authenticationOptions = builder.Configuration
    .GetSection(StructaDocAuthenticationOptions.SectionName)
    .Get<StructaDocAuthenticationOptions>() ?? new StructaDocAuthenticationOptions();
authenticationOptions.Validate();

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
builder.Services.AddStructaDocPersistence(databaseOptions);
builder.Services.AddStructaDocDocumentIngestion(ingestionOptions, storageOptions);
builder.Services.AddStructaDocParseProviders();
builder.Services.AddStructaDocProviderResults(
    providerResultOptions,
    providerResultNormalizationOptions);
builder.Services.AddStructaDocHostAuthentication(authenticationOptions);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ParseRunLeaseHeartbeat>();
builder.Services.AddHostedService<ParseRunMaintenanceWorker>();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = checked(ingestionOptions.MaxUploadBytes + (1024 * 1024)));

var app = builder.Build();

await app.Services.ApplyStructaDocMigrationsAsync(
    databaseOptions,
    app.Lifetime.ApplicationStopping);
await app.Services.BootstrapStructaDocAdministratorAsync(
    authenticationOptions,
    app.Lifetime.ApplicationStopping);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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

app.MapAdministratorSessionEndpoints(
    authenticationOptions.AdministratorSessionLifetime);
app.MapApiClientAdministrationEndpoints();
app.MapProviderConfigAdministrationEndpoints();
app.MapParseRunEndpoints();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
    });

app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program;
