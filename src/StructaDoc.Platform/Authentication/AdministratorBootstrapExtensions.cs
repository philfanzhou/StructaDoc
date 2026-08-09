using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructaDoc.Application.Authentication;
using StructaDoc.Platform.ControlPlane;
using StructaDoc.Platform.ControlPlane.Entities;

namespace StructaDoc.Platform.Authentication;

public static class AdministratorBootstrapExtensions
{
    /// <summary>
    /// Creates an administrator from deployment configuration. Interactive first-run setup is the
    /// normal path; this one exists so unattended deployments and CI can provision without a
    /// browser. Configuring it also closes first-run setup, because an administrator then exists.
    /// </summary>
    public static async Task BootstrapStructaDocAdministratorAsync(
        this IServiceProvider serviceProvider,
        StructaDocAuthenticationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (string.IsNullOrWhiteSpace(options.BootstrapAdministratorUsername))
        {
            return;
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var passwordHasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<AdminUserEntity>>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("StructaDoc.AdministratorBootstrap");
        var normalizedUsername = AdministratorUsernamePolicy.Normalize(
            options.BootstrapAdministratorUsername)!;

        if (await dbContext.AdminUsers.AnyAsync(
                user => user.NormalizedUsername == normalizedUsername,
                cancellationToken))
        {
            return;
        }

        var username = options.BootstrapAdministratorUsername.Trim();
        var user = new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            NormalizedUsername = normalizedUsername,
            DisplayName = string.IsNullOrWhiteSpace(options.BootstrapAdministratorDisplayName)
                ? username
                : options.BootstrapAdministratorDisplayName.Trim(),
            PasswordHash = string.Empty,
            IsActive = true,
            SecurityStamp = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
        };
        user.PasswordHash = passwordHasher.HashPassword(
            user,
            options.BootstrapAdministratorPassword!);
        dbContext.AdminUsers.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Created bootstrap administrator {AdministratorId}.",
                user.Id);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();

            if (!await dbContext.AdminUsers.AnyAsync(
                    candidate => candidate.NormalizedUsername == normalizedUsername,
                    cancellationToken))
            {
                throw;
            }
        }
    }
}
