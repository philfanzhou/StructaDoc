using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.ControlPlane.Entities;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class LegacyAdministratorMigrationTests
{
    [Fact]
    public async Task Upgrade_imports_legacy_administrator_before_the_business_table_is_removed()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var businessPath = Path.Combine(directory, "business.db");
            var controlPath = Path.Combine(directory, "control.db");
            var databaseOptions = new DatabaseOptions
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = $"Data Source={businessPath};Pooling=False",
                ApplyMigrationsOnStartup = true,
            };
            var services = new ServiceCollection();
            services.AddSingleton(databaseOptions);
            services.AddDbContext<StructaDocDbContext>(builder => builder.UseSqlite(
                databaseOptions.ConnectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(SqliteDesignTimeDbContextFactory).Assembly)));
            services.AddDbContext<ControlPlaneDbContext>(builder => builder.UseSqlite(
                $"Data Source={controlPath};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(ControlPlaneDesignTimeDbContextFactory).Assembly)));
            await using var provider = services.BuildServiceProvider();

            var administratorId = Guid.NewGuid();
            var securityStamp = Guid.NewGuid();
            const string email = "legacy.admin@example.test";
            const string password = "Legacy-Administrator-Password-2026!";
            var hasher = new PasswordHasher<AdminUserEntity>();
            var hashUser = new AdminUserEntity
            {
                Id = administratorId,
                Username = "legacy-admin",
                NormalizedUsername = "LEGACY-ADMIN",
                DisplayName = "Legacy Administrator",
                PasswordHash = string.Empty,
                IsActive = true,
                SecurityStamp = securityStamp,
                CreatedAtUtc = DateTime.UnixEpoch,
            };
            var passwordHash = hasher.HashPassword(hashUser, password);

            await using (var scope = provider.CreateAsyncScope())
            {
                var business = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
                var migrator = business.Database.GetService<IMigrator>();
                await migrator.MigrateAsync(
                    "20260807101514_AddUserWorkspaceLifecycleAndSegments",
                    TestContext.Current.CancellationToken);
                await business.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO admin_users
                        (id, display_name, email, normalized_email, password_hash, is_active,
                         security_stamp, created_at_utc, last_login_at_utc)
                    VALUES
                        ({administratorId}, {"Legacy Administrator"}, {email}, {email.ToUpperInvariant()},
                         {passwordHash}, {true}, {securityStamp}, {DateTime.UnixEpoch}, {null})
                    """, TestContext.Current.CancellationToken);

                var control = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                await control.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            await provider.MigrateLegacyAdministratorsAsync(
                databaseOptions,
                NullLogger.Instance,
                TestContext.Current.CancellationToken);
            await provider.MigrateLegacyAdministratorsAsync(
                databaseOptions,
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

            await using (var scope = provider.CreateAsyncScope())
            {
                var control = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var migrated = await control.AdminUsers.SingleAsync(
                    TestContext.Current.CancellationToken);
                Assert.Equal(administratorId, migrated.Id);
                Assert.Equal(email.ToUpperInvariant(), migrated.LegacyNormalizedLogin);
                Assert.Equal("Legacy Administrator", migrated.DisplayName);
                Assert.Equal(passwordHash, migrated.PasswordHash);
                Assert.True(migrated.IsActive);
                Assert.Equal(securityStamp, migrated.SecurityStamp);

                var authentication = new AdministratorAuthenticationService(
                    control,
                    hasher,
                    new AdministratorPasswordVerifier(hasher));
                var authenticated = await authentication.AuthenticateAsync(
                    email,
                    password,
                    DateTime.UtcNow,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(authenticated);
                Assert.Equal(administratorId, authenticated.Id);

                var business = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
                await business.Database.MigrateAsync(TestContext.Current.CancellationToken);
                var remainingLegacyTables = await business.Database.SqlQueryRaw<int>(
                        "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'admin_users'")
                    .SingleAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, remainingLegacyTables);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
