using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Persistence.Relational.DataProtection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// The Data Protection key ring, in the one form that can be used before the application is built.
///
/// Stored settings are read into configuration before dependency injection exists, and one of them is
/// encrypted, so the key ring has to be available earlier than the container that normally provides
/// it. The same instance is then registered for the rest of the application, which keeps a single
/// key ring rather than two readers of the same directory that could disagree about it.
/// </summary>
public static class StructaDocKeyRing
{
    /// <summary>
    /// Fixes the purpose chain across processes. A different name would produce keys that cannot
    /// decrypt anything written before, so it is set in one place rather than repeated.
    /// </summary>
    public const string ApplicationName = "StructaDoc";

    public static IDataProtectionProvider Create(StructaDocAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keyPath = Path.GetFullPath(options.DataProtectionKeysPath);
        Directory.CreateDirectory(keyPath);

        return DataProtectionProvider.Create(
            new DirectoryInfo(keyPath),
            builder => builder.SetApplicationName(ApplicationName));
    }

    /// <summary>
    /// The shared form: keys and revocations in the business database's
    /// <c>service_data_protection_keys</c> table, each row wrapped in an authenticated envelope
    /// under the deployment's root key, so every instance pointing at that database with that root
    /// key can decrypt what any instance encrypted.
    ///
    /// This is built on a minimal service provider rather than the application's, because the ring
    /// is needed before the host exists. The provider is never disposed: its singletons — the key
    /// repository and the context factory it resolves — live for the process, exactly like the
    /// directory the file form reads does. The repository creates its own context per operation and
    /// commits in its own transaction, so it never joins a caller's unit of work.
    /// </summary>
    public static IDataProtectionProvider CreateDatabase(
        StructaDocAuthenticationOptions options,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        var rootKey = options.DataProtectionRootKey;
        if (string.IsNullOrWhiteSpace(rootKey))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionRootKey must be configured when Authentication:DataProtectionKeyPersistence is Database.");
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<StructaDocDbContext>(
            dbContext => PersistenceServiceCollectionExtensions.ConfigureDatabase(
                dbContext,
                databaseOptions));
        services.AddDataProtection()
            .PersistKeysToServiceMantleEfCore<StructaDocDbContext>(
                ServiceId.Parse("structadoc"),
                _ => rootKey)
            // The same purpose chain as the file form, so material written by one form is not
            // silently re-derivable under another.
            .SetApplicationName(ApplicationName);

        var provider = services.BuildServiceProvider();
        // AddDataProtection resolves the provider that reads and writes through the registered
        // repository; holding this one instance is what keeps the process to a single key ring.
        return provider.GetRequiredService<IDataProtectionProvider>();
    }
}
