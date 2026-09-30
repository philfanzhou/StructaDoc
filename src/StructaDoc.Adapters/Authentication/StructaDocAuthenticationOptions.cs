using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Authentication;

public sealed class StructaDocAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string DataProtectionKeysPath { get; init; } = "./data/keys";

    /// <summary>
    /// Where the Data Protection key ring lives. <see cref="DataProtectionKeyPersistence.File"/> is
    /// the single-container default: a directory on the container's own volume, with no other
    /// moving part. <see cref="DataProtectionKeyPersistence.Database"/> shares the ring through the
    /// business database so multiple instances can decrypt each other's material; it requires the
    /// business database to be pinned by the deployment and a root key, because the ring has to
    /// exist before stored settings are decrypted and cannot live behind a connection string that
    /// itself needs decrypting.
    /// </summary>
    public DataProtectionKeyPersistence DataProtectionKeyPersistence { get; init; } = DataProtectionKeyPersistence.File;

    /// <summary>
    /// The root key that wraps every key-ring row in the database form, injected through the
    /// environment or deployment secrets. It is deliberately not a stored setting: an
    /// administrator reaching the service through a browser cannot be the custodian of the key
    /// that protects the store the browser writes to.
    /// </summary>
    public string? DataProtectionRootKey { get; init; }

    public TimeSpan AdministratorSessionLifetime { get; init; } = TimeSpan.FromHours(8);

    public int LoginPermitLimit { get; init; } = 10;

    public TimeSpan LoginRateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    public string? BootstrapAdministratorUsername { get; init; }

    public string? BootstrapAdministratorPassword { get; init; }

    public string? BootstrapAdministratorDisplayName { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataProtectionKeysPath))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionKeysPath must be configured.");
        }

        if (DataProtectionKeyPersistence == DataProtectionKeyPersistence.Database
            && string.IsNullOrWhiteSpace(DataProtectionRootKey))
        {
            throw new InvalidOperationException(
                "Authentication:DataProtectionRootKey must be configured when Authentication:DataProtectionKeyPersistence is Database.");
        }

        if (AdministratorSessionLifetime < TimeSpan.FromMinutes(5)
            || AdministratorSessionLifetime > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException(
                "Authentication:AdministratorSessionLifetime must be between 5 minutes and 7 days.");
        }

        if (LoginPermitLimit is < 1 or > 1000)
        {
            throw new InvalidOperationException(
                "Authentication:LoginPermitLimit must be between 1 and 1000.");
        }

        if (LoginRateLimitWindow < TimeSpan.FromSeconds(1)
            || LoginRateLimitWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Authentication:LoginRateLimitWindow must be between 1 second and 1 hour.");
        }

        var hasUsername = !string.IsNullOrWhiteSpace(BootstrapAdministratorUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(BootstrapAdministratorPassword);

        if (hasUsername != hasPassword)
        {
            throw new InvalidOperationException(
                "Bootstrap administrator username and password must be configured together.");
        }

        if (!hasUsername)
        {
            return;
        }

        if (!AdministratorUsernamePolicy.IsAcceptable(BootstrapAdministratorUsername))
        {
            throw new InvalidOperationException(
                $"Authentication:BootstrapAdministratorUsername must contain {AdministratorUsernamePolicy.MinimumLength} to {AdministratorUsernamePolicy.MaximumLength} letters, digits, '.', '_', or '-', and start and end with a letter or digit.");
        }

        if (!AdministratorPasswordPolicy.IsAcceptable(BootstrapAdministratorPassword))
        {
            throw new InvalidOperationException(
                $"Authentication:BootstrapAdministratorPassword must contain {AdministratorPasswordPolicy.MinimumLength} to {AdministratorPasswordPolicy.MaximumLength} characters.");
        }

        if (BootstrapAdministratorDisplayName?.Length > 255)
        {
            throw new InvalidOperationException(
                "Authentication:BootstrapAdministratorDisplayName cannot exceed 255 characters.");
        }
    }
}

/// <summary>
/// The two forms the key ring can take. The names are the configuration spellings, not C# words:
/// a deployment writes <c>Authentication:DataProtectionKeyPersistence=Database</c>.
/// </summary>
public enum DataProtectionKeyPersistence
{
    File = 0,

    Database = 1,
}

