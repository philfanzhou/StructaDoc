using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.ControlPlane.Entities;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Authentication;

public static class LegacyAdministratorMigrationExtensions
{
    private const string LegacyMigrationSuffix = "_MoveAdministratorsToControlPlane";
    private const string LegacyAuthenticationSuffix = "_AddAuthentication";

    /// <summary>
    /// Copies administrators from the former business-database table before the business migration
    /// that removes it. The copy is idempotent so a process may stop between this method and the
    /// business migration without losing either the old rows or the imported accounts.
    /// </summary>
    public static async Task MigrateLegacyAdministratorsAsync(
        this IServiceProvider serviceProvider,
        DatabaseOptions databaseOptions,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(logger);

        if (!databaseOptions.ApplyMigrationsOnStartup)
        {
            return;
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var controlPlane = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        if (await controlPlane.AdminUsers.AnyAsync(cancellationToken))
        {
            return;
        }

        var business = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var appliedMigrations = (await business.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToArray();
        if (appliedMigrations.Any(migration => migration.EndsWith(
                LegacyMigrationSuffix,
                StringComparison.Ordinal)))
        {
            return;
        }

        if (!appliedMigrations.Any(migration => migration.EndsWith(
                LegacyAuthenticationSuffix,
                StringComparison.Ordinal)))
        {
            return;
        }

        var legacyAdministrators = await ReadLegacyAdministratorsAsync(
            business.Database.GetDbConnection(),
            cancellationToken);
        if (legacyAdministrators.Count == 0)
        {
            return;
        }

        await using var transaction = await controlPlane.Database.BeginTransactionAsync(
            cancellationToken);

        var existing = await controlPlane.AdminUsers.ToListAsync(cancellationToken);
        var byId = existing.ToDictionary(user => user.Id);
        var legacyLogins = existing
            .Where(user => user.LegacyNormalizedLogin is not null)
            .ToDictionary(
                user => user.LegacyNormalizedLogin!,
                user => user.Id,
                StringComparer.Ordinal);
        var usernames = existing
            .Select(user => user.NormalizedUsername)
            .ToHashSet(StringComparer.Ordinal);
        var imported = 0;

        foreach (var legacy in legacyAdministrators)
        {
            if (byId.TryGetValue(legacy.Id, out var existingUser))
            {
                if (!string.Equals(
                        existingUser.LegacyNormalizedLogin,
                        legacy.NormalizedEmail,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Control-plane administrator '{legacy.Id:D}' conflicts with a legacy administrator.");
                }

                continue;
            }

            if (legacyLogins.TryGetValue(legacy.NormalizedEmail, out var conflictingId))
            {
                throw new InvalidOperationException(
                    $"Legacy administrator login '{legacy.Email}' conflicts with control-plane administrator '{conflictingId:D}'.");
            }

            var username = CreateUsername(legacy, usernames);
            var user = new AdminUserEntity
            {
                Id = legacy.Id,
                Username = username,
                NormalizedUsername = username.ToUpperInvariant(),
                LegacyNormalizedLogin = legacy.NormalizedEmail,
                DisplayName = legacy.DisplayName,
                PasswordHash = legacy.PasswordHash,
                IsActive = legacy.IsActive,
                SecurityStamp = legacy.SecurityStamp,
                CreatedAtUtc = legacy.CreatedAtUtc,
                LastLoginAtUtc = legacy.LastLoginAtUtc,
            };
            controlPlane.AdminUsers.Add(user);
            byId.Add(user.Id, user);
            legacyLogins.Add(user.LegacyNormalizedLogin, user.Id);
            usernames.Add(user.NormalizedUsername);
            imported++;
        }

        await controlPlane.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(
            "Migrated {AdministratorCount} legacy administrator account(s) to the local control plane. Legacy email logins remain valid.",
            imported);
    }

    private static async Task<IReadOnlyList<LegacyAdministrator>> ReadLegacyAdministratorsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var closeWhenComplete = connection.State != ConnectionState.Open;
        if (closeWhenComplete)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, display_name, email, normalized_email, password_hash, is_active,
                       security_stamp, created_at_utc, last_login_at_utc
                FROM admin_users
                ORDER BY id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var administrators = new List<LegacyAdministrator>();
            while (await reader.ReadAsync(cancellationToken))
            {
                administrators.Add(new LegacyAdministrator(
                    ReadGuid(reader, 0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ReadBoolean(reader, 5),
                    ReadGuid(reader, 6),
                    ReadUtcDateTime(reader, 7),
                    reader.IsDBNull(8) ? null : ReadUtcDateTime(reader, 8)));
            }

            return administrators;
        }
        finally
        {
            if (closeWhenComplete)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string CreateUsername(
        LegacyAdministrator administrator,
        ISet<string> normalizedUsernames)
    {
        var localPart = administrator.Email.Split('@', 2)[0];
        var builder = new StringBuilder(localPart.Length);
        foreach (var character in localPart)
        {
            if (character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '.' or '_' or '-')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('-');
            }
        }

        var baseName = builder.ToString().Trim('.', '_', '-');
        if (baseName.Length < 3)
        {
            baseName = "admin";
        }

        const int suffixLength = 9;
        var maximumBaseLength = AdministratorUsernamePolicy.MaximumLength - suffixLength;
        if (baseName.Length > maximumBaseLength)
        {
            baseName = baseName[..maximumBaseLength].TrimEnd('.', '_', '-');
        }

        var id = administrator.Id.ToString("N", CultureInfo.InvariantCulture);
        for (var suffixCharacters = 8; suffixCharacters <= id.Length; suffixCharacters += 4)
        {
            var candidate = $"{baseName}-{id[..suffixCharacters]}";
            if (normalizedUsernames.Add(candidate.ToUpperInvariant()))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not create a unique username for legacy administrator '{administrator.Id:D}'.");
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is Guid guid
            ? guid
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static bool ReadBoolean(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is bool boolean
            ? boolean
            : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    private static DateTime ReadUtcDateTime(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        var dateTime = value switch
        {
            DateTime typed => typed,
            DateTimeOffset offset => offset.UtcDateTime,
            _ => DateTime.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
        };
        return dateTime.Kind == DateTimeKind.Utc
            ? dateTime
            : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }

    private sealed record LegacyAdministrator(
        Guid Id,
        string DisplayName,
        string Email,
        string NormalizedEmail,
        string PasswordHash,
        bool IsActive,
        Guid SecurityStamp,
        DateTime CreatedAtUtc,
        DateTime? LastLoginAtUtc);
}
