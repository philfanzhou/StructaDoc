using StructaDoc.Application.Authentication;

namespace StructaDoc.Platform.Authentication;

public sealed class StructaDocAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string DataProtectionKeysPath { get; init; } = "./data/keys";

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
