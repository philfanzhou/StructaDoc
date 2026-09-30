using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Migration;
using StructaDoc.Adapters.Authentication;

namespace StructaDoc.Adapters.Persistence;

/// <summary>
/// The business database's migration executor: the one migration workflow both application startup
/// and the one-shot migration command run, executed by the ServiceMantle orchestration under the
/// provider lease. The step order is fixed: InnoDB preflight, legacy administrator import when the
/// database already exists, then the migrations of the configured assembly.
/// </summary>
public sealed class StructaDocMigrationExecutor(
    IServiceProvider serviceProvider,
    StructaDocDbContext dbContext,
    IBusinessDatabaseMigrationPreflight preflight,
    DatabaseOptions databaseOptions,
    ILogger<StructaDocMigrationExecutor> logger) : IDatabaseMigrationExecutor
{
    /// <summary>
    /// Compares the applied entries of <c>__EFMigrationsHistory</c> with the migration list of the
    /// configured migration assembly. History entries the application does not define mean the
    /// database is newer than this build and migration must fail closed.
    /// </summary>
    public async ValueTask<MigrationObservationState> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var defined = dbContext.Database.GetMigrations().ToArray();
            var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken))
                .ToArray();

            if (applied.Length == 0)
            {
                return MigrationObservationState.Empty;
            }

            var definedSet = new HashSet<string>(defined, StringComparer.Ordinal);
            return applied.Any(applied => !definedSet.Contains(applied))
                ? MigrationObservationState.VersionTooNew
                : defined.Length == applied.Length
                    ? MigrationObservationState.CurrentVersionCompatible
                    : MigrationObservationState.PendingMigration;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error,
                "The business database migration history could not be inspected.");
            return MigrationObservationState.InspectionFailed;
        }
    }

    /// <summary>
    /// Runs the complete business database migration workflow. The preflight and the legacy import
    /// run before the assembly migrations for the same reason they always have: the import must
    /// happen before the migration that removes the legacy table, and the preflight must gate the
    /// DDL before it is attempted. A failure is logged with its full detail here, because the
    /// orchestration result the caller sees deliberately carries only a stable error code.
    /// </summary>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var preflightResult = await preflight.CheckAsync(databaseOptions, cancellationToken);

            // The historical business-database administrator table is removed by a later migration.
            // Import it before that migration can run. A database the server preflight proved absent has
            // no legacy table, and opening its qualified connection here would replace the actionable
            // preflight result with an unknown-database error.
            if (preflightResult.DatabaseExists)
            {
                await serviceProvider.MigrateLegacyAdministratorsAsync(
                    databaseOptions,
                    logger,
                    cancellationToken);
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error,
                "The business database migration workflow failed. The orchestration result reports a stable error code; this entry carries the actionable detail.");
            throw;
        }
    }
}
