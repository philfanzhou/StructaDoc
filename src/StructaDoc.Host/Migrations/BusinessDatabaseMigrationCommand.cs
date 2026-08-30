using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Migrations;

/// <summary>
/// Runs the schema work required before a new application version starts, without starting the web
/// server or any background service.
/// </summary>
public static class BusinessDatabaseMigrationCommand
{
    public const string Flag = "--migrate-business-database";

    /// <summary>
    /// Finds and removes the value-less operation flag before any host or configuration builder sees
    /// the command line. <c>AddCommandLine</c> treats an unknown value-less argument as malformed.
    /// </summary>
    public static bool TryExtractArguments(string[] arguments, out string[] remainingArguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.Contains(Flag, StringComparer.Ordinal))
        {
            remainingArguments = arguments;
            return false;
        }

        remainingArguments = arguments
            .Where(argument => !string.Equals(argument, Flag, StringComparison.Ordinal))
            .ToArray();
        return true;
    }

    /// <summary>
    /// Executes the one-shot migration against deployment configuration. The operation flag must
    /// already have been removed by <see cref="TryExtractArguments"/>.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        string[] deploymentArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deploymentArguments);

        string? submittedConnectionString = null;
        ILogger? logger = null;
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                deploymentArguments);
            builder.Configuration.AddContainerDefaults(deploymentArguments);
            // Database Providers can log the original exception before this command reaches its
            // sanitized failure boundary. Suppress their diagnostics here; the final command log
            // carries the safe, actionable summary instead.
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);

            var controlPlaneOptions = builder.Configuration
                .GetSection(ControlPlaneOptions.SectionName)
                .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
            submittedConnectionString = builder.Configuration[
                $"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.ConnectionString)}"];

            // Build the control-plane provider before binding or validating the business database.
            // A broken business setting must not prevent the control plane from reaching the current
            // schema, because it is part of the recovery set and the first migration target.
            controlPlaneOptions.Validate();
            builder.Services.AddStructaDocControlPlane(controlPlaneOptions);

            await using var controlPlaneProvider = builder.Services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            logger = controlPlaneProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("StructaDoc.BusinessDatabaseMigration");

            logger.LogInformation("Migrating the StructaDoc control-plane database.");
            await controlPlaneProvider.ApplyStructaDocControlPlaneMigrationsAsync(cancellationToken);

            var configuredDatabaseOptions = builder.Configuration
                .GetSection(DatabaseOptions.SectionName)
                .Get<DatabaseOptions>() ?? new DatabaseOptions();
            var databaseOptions = new DatabaseOptions
            {
                Provider = configuredDatabaseOptions.Provider,
                ConnectionString = configuredDatabaseOptions.ConnectionString,
                ServerVersion = configuredDatabaseOptions.ServerVersion,
                // An explicit migration command is the operator's authorization to change the
                // schema. The normal-startup switch therefore cannot disable this operation.
                ApplyMigrationsOnStartup = true,
            };
            databaseOptions.Validate();

            builder.Services.AddStructaDocPersistenceMigrationServices(databaseOptions);
            await using var migrationProvider = builder.Services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });

            logger.LogInformation(
                "Checking the {DatabaseProvider} business database before migration.",
                databaseOptions.Provider);
            var migrationPreflight = migrationProvider
                .GetRequiredService<IBusinessDatabaseMigrationPreflight>();
            var preflightResult = await migrationPreflight.CheckAsync(
                databaseOptions,
                cancellationToken);

            if (preflightResult.DatabaseExists)
            {
                await migrationProvider.MigrateLegacyAdministratorsAsync(
                    databaseOptions,
                    logger,
                    cancellationToken);
            }

            await migrationProvider.ApplyStructaDocMigrationsAsync(
                databaseOptions,
                cancellationToken);
            logger.LogInformation("StructaDoc database migration completed successfully.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteFailure(
                logger,
                "StructaDoc database migration was cancelled before it completed.");
            return 1;
        }
        catch (Exception error)
        {
            var detail = DatabaseConnectionProbe.SanitizeMessage(
                error,
                submittedConnectionString);
            var diagnostic = string.IsNullOrWhiteSpace(detail)
                ? "Review the deployment-supplied ControlPlane and Database configuration and the database server logs."
                : detail;
            WriteFailure(logger, $"StructaDoc database migration failed. {diagnostic}");
            return 1;
        }
    }

    private static void WriteFailure(ILogger? logger, string message)
    {
        if (logger is null)
        {
            Console.Error.WriteLine(message);
            return;
        }

        logger.LogError("{MigrationFailure}", message);
    }
}
