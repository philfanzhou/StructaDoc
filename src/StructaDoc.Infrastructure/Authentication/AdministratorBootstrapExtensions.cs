using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Authentication;

public static class AdministratorBootstrapExtensions
{
    public static async Task BootstrapStructaDocAdministratorAsync(
        this IServiceProvider serviceProvider,
        StructaDocAuthenticationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (string.IsNullOrWhiteSpace(options.BootstrapAdministratorEmail))
        {
            return;
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var passwordHasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<AdminUserEntity>>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("StructaDoc.AdministratorBootstrap");
        var normalizedEmail = AdministratorAuthenticationService.NormalizeEmail(
            options.BootstrapAdministratorEmail)!;

        if (await dbContext.AdminUsers.AnyAsync(
                user => user.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            return;
        }

        var email = options.BootstrapAdministratorEmail.Trim();
        var user = new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            DisplayName = string.IsNullOrWhiteSpace(options.BootstrapAdministratorDisplayName)
                ? email
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
                    candidate => candidate.NormalizedEmail == normalizedEmail,
                    cancellationToken))
            {
                throw;
            }
        }
    }
}
