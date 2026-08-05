using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StructaDoc.Contracts.System;
using StructaDoc.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
builder.Services.AddStructaDocPersistence(databaseOptions);

var app = builder.Build();

await app.Services.ApplyStructaDocMigrationsAsync(
    databaseOptions,
    app.Lifetime.ApplicationStopping);

var serviceVersion = Assembly
    .GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";

app.MapGet(
        "/api/v1/system/info",
        () => new ServiceInfoResponse("StructaDoc", serviceVersion))
    .WithName("GetServiceInfo");

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live"),
    });

app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program;
