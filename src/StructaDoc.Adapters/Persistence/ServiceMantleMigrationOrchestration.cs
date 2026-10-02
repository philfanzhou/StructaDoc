using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb.Migration;
using ServiceMantle.Database.MySql.Migration;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Migration;

namespace StructaDoc.Adapters.Persistence;

/// <summary>
/// Composes the ServiceMantle migration orchestration for the business database: maps deployment
/// database options onto the library's bootstrap model, owns the provider-specific lease
/// registrations, and fixes the deployment mode each provider supports.
/// </summary>
public static class ServiceMantleMigrationOrchestration
{
    /// <summary>
    /// The identity the migration lease is scoped to. One StructaDoc deployment shares one id, so
    /// every instance and the one-shot migration command contend for the same lease.
    /// </summary>
    public static ServiceId MigrationServiceId { get; } = ServiceId.Parse("structadoc");

    /// <summary>
    /// How long a starting instance waits for the migration lease before failing. Not
    /// configurable: a deployment that needs longer than this to wait out a peer's migration is a
    /// deployment whose container restart policy is the right retry mechanism.
    /// </summary>
    public static TimeSpan DefaultLockAcquireTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Maps deployment database options onto the library's bootstrap model.</summary>
    public static BootstrapDatabaseConfiguration ToBootstrapDatabaseConfiguration(
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);

        var connectionString = databaseOptions.Provider == DatabaseProvider.Sqlite
            ? ResolveSqliteBootstrapConnectionString(databaseOptions.ConnectionString)
            : databaseOptions.ConnectionString;

        return new BootstrapDatabaseConfiguration(
            MapProviderId(databaseOptions.Provider),
            databaseOptions.ServerVersion,
            connectionString);
    }

    /// <summary>
    /// The deployment mode StructaDoc supports per provider. SQLite is single-instance by design
    /// (see the database support documentation); the server databases take the multi-instance
    /// lease path.
    /// </summary>
    public static DatabaseDeploymentMode ResolveDeploymentMode(DatabaseProvider provider) =>
        provider == DatabaseProvider.Sqlite
            ? DatabaseDeploymentMode.SingleInstance
            : DatabaseDeploymentMode.MultiInstance;

    /// <summary>
    /// The provider-specific migration leases. SQLite is deliberately absent: it has no
    /// cross-process lease, and its single-instance turn is coordinated through the deployment
    /// capability registry instead.
    /// </summary>
    public static DatabaseMigrationLockProviderRegistry CreateMigrationLockProviderRegistry() => new(
        [
            new PostgreSqlMigrationLockProvider(),
            new MySqlMigrationLockProvider(),
            new MariaDbMigrationLockProvider(),
        ],
        DatabaseProviderIdResolver.Empty);

    /// <summary>
    /// The deployment capability declarations consulted before any migration side effect.
    /// SQLite is single-instance only; server databases declare multi-instance support and
    /// always use their real provider lease. These declarations perform no target preparation.
    /// </summary>
    public static DatabaseDeploymentCapabilityRegistry CreateDeploymentCapabilityRegistry() => new(
        [
            new SqliteDatabaseTargetPreparationProvider(),
            new ServerMigrationCapability(WellKnownDatabaseProviderIds.PostgreSql),
            new ServerMigrationCapability(WellKnownDatabaseProviderIds.MySql),
            new ServerMigrationCapability(WellKnownDatabaseProviderIds.MariaDb),
        ],
        DatabaseProviderIdResolver.Empty);

    /// <summary>
    /// Runs one migration orchestration session for the business database through the shared
    /// registrations. Both application startup and the one-shot migration command call this.
    /// </summary>
    /// <param name="serviceProvider">
    /// A provider carrying the registrations made by
    /// <c>AddStructaDocPersistenceMigrationServices</c>.
    /// </param>
    /// <param name="databaseOptions">The deployment database configuration.</param>
    /// <param name="deploymentMode">
    /// The requested deployment mode. SQLite validates it against the declared capability before
    /// any side effect; the server databases always take the real-lease path regardless of mode.
    /// </param>
    /// <param name="lockAcquireTimeout">
    /// The lease acquisition timeout, or <see langword="null"/> for
    /// <see cref="DefaultLockAcquireTimeout"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates caller cancellation into every stage.</param>
    /// <returns>The orchestration result, whose failure codes are safe to log.</returns>
    public static async Task<StartupDatabaseGateResult> OrchestrateStructaDocMigrationsAsync(
        this IServiceProvider serviceProvider,
        DatabaseOptions databaseOptions,
        DatabaseDeploymentMode deploymentMode,
        TimeSpan? lockAcquireTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        cancellationToken.ThrowIfCancellationRequested();
        if (databaseOptions.Provider == DatabaseProvider.Sqlite)
        {
            // Directory preparation is the only local policy the shared gate cannot perform.
            // Validate first so an unsupported deployment cannot create any filesystem entry.
            var capabilities = serviceProvider
                .GetRequiredService<DatabaseDeploymentCapabilityRegistry>();
            var validation = new DatabaseDeploymentValidator(capabilities)
                .Validate(WellKnownDatabaseProviderIds.Sqlite, deploymentMode);
            if (!validation.IsSupported)
            {
                return StartupDatabaseGateResult.Failure(
                    validation.MigrationErrorCode ?? WellKnownMigrationErrorCodes.LockNotSupported);
            }
        }

        var bootstrap = ToBootstrapDatabaseConfiguration(databaseOptions);
        var options = new StartupDatabaseGateOptions(
            bootstrap,
            databaseOptions.Provider == DatabaseProvider.Sqlite
                ? deploymentMode
                : DatabaseDeploymentMode.MultiInstance,
            lockAcquireTimeout ?? DefaultLockAcquireTimeout,
            enableTargetPreparation: false,
            allowTargetCreation: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (databaseOptions.Provider == DatabaseProvider.Sqlite)
        {
            EnsureSqliteDirectoryExists(bootstrap.ConnectionString);
        }

        // Every call owns a fresh receipt; it is neither persisted nor a readiness source.
        // Server targets must already exist: there is no unleased EF fallback or preparation.
        return await serviceProvider.GetRequiredService<StartupDatabaseGate>().RunAsync(
            options,
            new StartupDatabaseReceipt(),
            MigrationServiceId,
            cancellationToken);
    }

    /// <summary>Canonicalizes provider enum values onto the library's provider ids.</summary>
    private static string MapProviderId(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => WellKnownDatabaseProviderIds.Sqlite,
        DatabaseProvider.PostgreSql => WellKnownDatabaseProviderIds.PostgreSql,
        DatabaseProvider.MySql => WellKnownDatabaseProviderIds.MySql,
        DatabaseProvider.MariaDb => WellKnownDatabaseProviderIds.MariaDb,
        _ => throw new InvalidOperationException(
            $"Unsupported database provider '{provider}'."),
    };

    /// <summary>
    /// The library's SQLite target identity accepts fully-qualified local file paths only, while
    /// the shipped development default is relative and Entity Framework resolves it against the
    /// working directory. The bootstrap copy is normalized the same way, and symbolic links in the
    /// path are resolved to the physical location, because the identity's single-instance
    /// serialization must be keyed by the file that is actually opened, not by one of its spellings
    /// (on macOS the system temporary directory itself lives behind a symbolic link). The database
    /// options themselves stay byte-for-byte what the deployment supplied.
    /// </summary>
    private static string ResolveSqliteBootstrapConnectionString(string connectionString)
    {
        try
        {
            var resolved = SqliteDataSource.ResolveConnectionString(
                connectionString,
                Path.GetFullPath(Directory.GetCurrentDirectory()));
            var builder = new SqliteConnectionStringBuilder(resolved);
            if (string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return resolved;
            }

            // The shared resolver anchors paths but intentionally does not resolve links.
            builder.DataSource = ResolvePhysicalPath(builder.DataSource);
            return builder.ConnectionString;
        }
        catch (Exception error) when (error is ArgumentException or FormatException
            or NotSupportedException or PathTooLongException)
        {
            // Do not expose parser inner exceptions, which can contain submitted secrets.
            throw new ArgumentException("The SQLite business database path is invalid.");
        }
    }

    /// <summary>
    /// Makes a data source absolute and resolves the symbolic links that exist along it. Segments
    /// that do not exist yet — the leaf of a not-yet-created database, or its not-yet-created
    /// parent — are kept as spelled, because a path that does not exist cannot be an alias for
    /// another one.
    /// </summary>
    private static string ResolvePhysicalPath(string dataSource)
    {
        var path = Path.GetFullPath(dataSource);
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || path.Length <= root.Length)
        {
            return path;
        }

        var current = root;
        var segments = path[root.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var candidate = Path.Join(current, segment);
            FileSystemInfo? entry = File.Exists(candidate)
                ? new FileInfo(candidate)
                : Directory.Exists(candidate) ? new DirectoryInfo(candidate) : null;
            if (entry is null)
            {
                current = candidate;
                continue;
            }

            try
            {
                if (entry.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    candidate = ResolvePhysicalPath(target.FullName);
                }
            }
            catch (IOException)
            {
                // A link that cannot be resolved is left as spelled; the library's target
                // inspection is the authority that fails closed on it.
            }

            current = candidate;
        }

        return current;
    }

    // The shared server packages supply leases but no deployment declarations. StructaDoc's
    // server path always selects MultiInstance, so no process-local target identity is needed.
    private sealed class ServerMigrationCapability(string providerId) : IDatabaseDeploymentCapabilityProvider
    {
        public DatabaseDeploymentCapability Capability { get; } = new(
            providerId, DatabaseDeploymentSupport.SingleAndMultiInstance);

        public ValueTask<string> GetCanonicalTargetIdentityAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Server migrations require a provider lease.");
    }

    private static void EnsureSqliteDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
