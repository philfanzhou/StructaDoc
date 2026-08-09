using Microsoft.EntityFrameworkCore;

namespace StructaDoc.Adapters.Persistence;

/// <summary>
/// Why a candidate business database could not be used. The codes are stable tokens the web
/// interface translates; the service does not guess the reader's language.
/// </summary>
public enum DatabaseProbeCode
{
    /// <summary>The database answered and carries the schema this build expects.</summary>
    Reachable,

    /// <summary>The database answered but has not been migrated to this build yet.</summary>
    ReachableWithPendingMigrations,

    /// <summary>The values do not describe a database this build can open at all.</summary>
    InvalidConfiguration,

    /// <summary>Nothing answered, or what answered refused the connection.</summary>
    Unreachable,

    TimedOut,
}

public sealed record DatabaseProbeResult(DatabaseProbeCode Code, string Detail)
{
    public bool Succeeded => Code is DatabaseProbeCode.Reachable
        or DatabaseProbeCode.ReachableWithPendingMigrations;
}

/// <summary>
/// Opens a candidate business database and reports whether it can be used, so an administrator
/// finds out before saving rather than after a restart that does not come back.
///
/// The probe connects and reads migration history only. It creates nothing: a connection string
/// pointing at the wrong database must not leave StructaDoc tables behind in it.
/// </summary>
public sealed class DatabaseConnectionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<DatabaseProbeResult> ProbeAsync(
        DatabaseOptions candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        DbContextOptionsBuilder<StructaDocDbContext> builder;
        try
        {
            candidate.Validate();
            builder = new DbContextOptionsBuilder<StructaDocDbContext>();
            PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, candidate);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
        {
            return new DatabaseProbeResult(DatabaseProbeCode.InvalidConfiguration, SanitizeMessage(error, candidate.ConnectionString));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await using var dbContext = new StructaDocDbContext(builder.Options);
            if (!await dbContext.Database.CanConnectAsync(deadline.Token))
            {
                return new DatabaseProbeResult(DatabaseProbeCode.Unreachable, string.Empty);
            }

            var pending = await dbContext.Database.GetPendingMigrationsAsync(deadline.Token);
            return pending.Any()
                ? new DatabaseProbeResult(
                    DatabaseProbeCode.ReachableWithPendingMigrations,
                    $"{pending.Count()}")
                : new DatabaseProbeResult(DatabaseProbeCode.Reachable, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DatabaseProbeResult(DatabaseProbeCode.TimedOut, string.Empty);
        }
        catch (Exception error)
        {
            return new DatabaseProbeResult(DatabaseProbeCode.Unreachable, SanitizeMessage(error, candidate.ConnectionString));
        }
    }

    /// <summary>
    /// A database driver's message is the only useful part of a failed connection, but it is written
    /// by something that was handed a connection string and may quote it back. Anything containing
    /// the submitted string is dropped rather than repeated to a browser.
    /// </summary>
    public static string SanitizeMessage(Exception error, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(error);

        var message = error.Message;
        if (string.IsNullOrWhiteSpace(message)
            || (!string.IsNullOrEmpty(connectionString)
                && message.Contains(connectionString, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return message.Length > 200 ? message[..200] : message;
    }
}
