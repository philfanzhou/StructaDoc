using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.ControlPlane.Entities;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocAuthenticationPersistence(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPasswordHasher<AdminUserEntity>, PasswordHasher<AdminUserEntity>>();
        services.AddSingleton<AdministratorPasswordVerifier>();
        services.AddScoped<IAdministratorAuthenticationService, AdministratorAuthenticationService>();
        services.AddScoped<IAdministratorProvisioningService, AdministratorProvisioningService>();
        services.AddScoped<IAdministratorAccountService, AdministratorAccountService>();
        services.AddScoped<IApiClientAdministrationService, ApiClientAdministrationService>();
        return services;
    }
}
