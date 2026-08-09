using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Storage;

namespace StructaDoc.Host.Settings;

/// <summary>
/// Where documents are kept and where business data lives, reported and testable from the browser.
///
/// These two are settings like any other, but they are the only ones whose wrong value leaves the
/// service running with nothing working, so they get a status of their own and a way to try a
/// configuration before committing to it. Neither ever sends back a credential or a connection
/// string; both report only what is in force and whether it can be reached.
/// </summary>
public static class InfrastructureSettingsEndpoints
{
    public static IEndpointRouteBuilder MapInfrastructureSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/settings")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/storage", GetStorageStatus).Produces<StorageStatusResponse>();
        group.MapPost("/storage/test", TestStorageAsync)
            .Produces<ConnectionTestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/database", GetDatabaseStatusAsync).Produces<DatabaseStatusResponse>();
        group.MapPost("/database/test", TestDatabaseAsync)
            .Produces<ConnectionTestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static IResult GetStorageStatus(
        FileStorageOptions options,
        SettingsStartupFault fault)
    {
        return Results.Ok(new StorageStatusResponse(
            options.Provider,
            fault.DetailFor(SettingCatalog.StorageSection),
            options.AccessKey is not null));
    }

    /// <summary>
    /// Reported by asking the database rather than by remembering how startup went, because the
    /// interesting case is a database that went away after the service came up.
    /// </summary>
    private static async Task<IResult> GetDatabaseStatusAsync(
        DatabaseOptions options,
        SettingsStartupFault fault,
        DatabaseConnectionProbe probe,
        CancellationToken cancellationToken)
    {
        var result = await probe.ProbeAsync(options, cancellationToken);
        return Results.Ok(new DatabaseStatusResponse(
            options.Provider.ToString(),
            fault.DetailFor(SettingCatalog.DatabaseSection),
            result.Succeeded,
            result.Code == DatabaseProbeCode.ReachableWithPendingMigrations));
    }

    private static async Task<IResult> TestStorageAsync(
        StorageConnectionTestRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        FileStorageOptions inForce,
        StorageConnectionProbe probe,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        // An omitted field falls back to what is in force. A secret the service never sends back
        // cannot be retyped just to test the bucket name next to it.
        var candidate = new FileStorageOptions
        {
            Provider = Fallback(request.Provider, inForce.Provider)!,
            RootPath = Fallback(request.RootPath, inForce.RootPath)!,
            ServiceUrl = Fallback(request.ServiceUrl, inForce.ServiceUrl),
            Region = Fallback(request.Region, inForce.Region),
            Bucket = Fallback(request.Bucket, inForce.Bucket),
            Prefix = Fallback(request.Prefix, inForce.Prefix)!,
            AccessKey = Fallback(request.AccessKey, inForce.AccessKey),
            SecretKey = Fallback(request.SecretKey, inForce.SecretKey),
            ForcePathStyle = request.ForcePathStyle ?? inForce.ForcePathStyle,
        };

        var result = await probe.ProbeAsync(candidate, cancellationToken);
        return Results.Ok(new ConnectionTestResponse(
            result.Succeeded,
            result.Code.ToString(),
            result.Detail));
    }

    private static async Task<IResult> TestDatabaseAsync(
        DatabaseConnectionTestRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        DatabaseOptions inForce,
        DatabaseConnectionProbe probe,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var provider = inForce.Provider;
        if (!string.IsNullOrWhiteSpace(request.Provider)
            && !Enum.TryParse(request.Provider, ignoreCase: true, out provider))
        {
            return Results.Ok(new ConnectionTestResponse(
                false,
                nameof(DatabaseProbeCode.InvalidConfiguration),
                $"'{request.Provider}' is not a database this build supports."));
        }

        var candidate = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = Fallback(request.ConnectionString, inForce.ConnectionString)!,
            ServerVersion = Fallback(request.ServerVersion, inForce.ServerVersion),
            ApplyMigrationsOnStartup = false,
        };

        var result = await probe.ProbeAsync(candidate, cancellationToken);
        return Results.Ok(new ConnectionTestResponse(
            result.Succeeded,
            result.Code.ToString(),
            result.Detail));
    }

    private static string? Fallback(string? submitted, string? inForce) =>
        string.IsNullOrWhiteSpace(submitted) ? inForce : submitted.Trim();
}
