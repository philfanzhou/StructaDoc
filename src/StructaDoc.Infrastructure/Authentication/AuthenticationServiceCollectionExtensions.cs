using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Authentication;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocAuthenticationPersistence(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPasswordHasher<AdminUserEntity>, PasswordHasher<AdminUserEntity>>();
        services.AddSingleton<AdministratorPasswordVerifier>();
        services.AddScoped<IAdministratorAuthenticationService, AdministratorAuthenticationService>();
        return services;
    }
}
