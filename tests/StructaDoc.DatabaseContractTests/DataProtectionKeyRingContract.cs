using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Persistence.Relational.DataProtection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Host.Authentication;

namespace StructaDoc.DatabaseContractTests;

/// <summary>
/// The Data Protection key ring's database contract from #114: the business database migration
/// creates <c>service_data_protection_keys</c> on every supported database, and a key ring built
/// against that database decrypts what another instance encrypted, while a wrong root key fails
/// closed. Key and revocation XML only ever lands in the table as an <c>sm:v1:</c> envelope.
/// </summary>
internal static class DataProtectionKeyRingContract
{
    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
            ApplyMigrationsOnStartup = true,
        };

        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(optionsBuilder, databaseOptions);
        await using (var context = new StructaDocDbContext(optionsBuilder.Options))
        {
            // The table is part of the ordinary migration history: no separate entry point and no
            // ad-hoc DDL create it.
            await context.Database.MigrateAsync(cancellationToken);
        }

        var first = StructaDocKeyRing.CreateDatabase(
            AuthenticationWithRootKey("contract-root-key-one"),
            databaseOptions);
        var protectedValue = first.CreateProtector("contract").Protect("cross-instance secret");

        // A separately constructed ring is what another process resolves: same database, same root
        // key, no shared in-memory state.
        var second = StructaDocKeyRing.CreateDatabase(
            AuthenticationWithRootKey("contract-root-key-one"),
            databaseOptions);
        Assert.Equal(
            "cross-instance secret",
            second.CreateProtector("contract").Unprotect(protectedValue));

        // Revocation is ring state like the keys themselves: written through one instance, read by
        // the next, and material protected before the revocation no longer decrypts.
        var revoking = BuildRing(databaseOptions, "contract-root-key-one");
        revoking.Keys.RevokeAllKeys(DateTimeOffset.UtcNow, "contract test");
        var afterRevocation = StructaDocKeyRing.CreateDatabase(
            AuthenticationWithRootKey("contract-root-key-one"),
            databaseOptions);
        Assert.ThrowsAny<CryptographicException>(
            () => afterRevocation.CreateProtector("contract").Unprotect(protectedValue));

        // A wrong root key cannot open the stored envelopes and fails closed, without either root
        // key in the error.
        var wrong = StructaDocKeyRing.CreateDatabase(
            AuthenticationWithRootKey("contract-root-key-two"),
            databaseOptions);
        var error = Assert.ThrowsAny<Exception>(
            () => wrong.CreateProtector("contract").Unprotect(protectedValue));
        var text = error.ToString();
        Assert.DoesNotContain("contract-root-key-one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-root-key-two", text, StringComparison.Ordinal);

        // Every stored row is an envelope, not plaintext XML: the key material never appears in the
        // table itself.
        await using (var context = new StructaDocDbContext(optionsBuilder.Options))
        {
            var rows = await context.Database
                .SqlQuery<string>($"SELECT encrypted_xml AS Value FROM service_data_protection_keys")
                .ToListAsync(cancellationToken);
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.StartsWith("sm:v1:", row, StringComparison.Ordinal));
        }
    }

    private static StructaDocAuthenticationOptions AuthenticationWithRootKey(string rootKey) => new()
    {
        DataProtectionKeyPersistence = DataProtectionKeyPersistence.Database,
        DataProtectionRootKey = rootKey,
    };

    /// <summary>
    /// The registration shape the Host itself uses, built here to reach the key manager for the
    /// revocation step. Keeping it in the contract means the wiring stays a visible, tested fact
    /// rather than an implementation detail of the Host's startup.
    /// </summary>
    private static (IDataProtectionProvider Ring, IKeyManager Keys) BuildRing(
        DatabaseOptions databaseOptions,
        string rootKey)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<StructaDocDbContext>(
            dbContext => PersistenceServiceCollectionExtensions.ConfigureDatabase(dbContext, databaseOptions));
        services.AddDataProtection()
            .PersistKeysToServiceMantleEfCore<StructaDocDbContext>(
                ServiceId.Parse("structadoc"),
                _ => rootKey)
            .SetApplicationName(StructaDocKeyRing.ApplicationName);
        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetRequiredService<IKeyManager>());
    }
}
