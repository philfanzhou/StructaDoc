namespace StructaDoc.Adapters.Persistence;

public static class DatabaseMigrationExtensions
{
    public static async Task ApplyStructaDocMigrationsAsync(
        this IServiceProvider serviceProvider,
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        if (!databaseOptions.ApplyMigrationsOnStartup)
        {
            return;
        }

        var result = await serviceProvider.OrchestrateStructaDocMigrationsAsync(
            databaseOptions,
            ServiceMantleMigrationOrchestration.ResolveDeploymentMode(databaseOptions.Provider),
            cancellationToken: cancellationToken);

        if (!result.Succeeded)
        {
            // The orchestration result is safe by library contract; turning it into an exception
            // here keeps the existing failure boundary, where the message is sanitized and startup
            // decides between a recorded fault and stopping.
            throw new InvalidOperationException(
                $"Business database migration orchestration failed ({result.ErrorCode}).");
        }
    }
}
