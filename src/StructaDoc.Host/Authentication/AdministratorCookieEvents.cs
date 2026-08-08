using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Infrastructure.ControlPlane;

namespace StructaDoc.Host.Authentication;

public sealed class AdministratorCookieEvents(ControlPlaneDbContext dbContext)
    : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var subjectType = context.Principal?.FindFirstValue(StructaDocClaimTypes.SubjectType);
        if (string.Equals(subjectType, SubjectTypes.User, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue(StructaDocClaimTypes.ExternalIssuer))
                || string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue(StructaDocClaimTypes.ExternalSubject)))
            {
                context.RejectPrincipal();
            }

            return;
        }

        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stampValue = context.Principal?.FindFirstValue(StructaDocClaimTypes.SecurityStamp);

        if (!Guid.TryParse(idValue, out var administratorId)
            || !Guid.TryParse(stampValue, out var securityStamp))
        {
            context.RejectPrincipal();
            return;
        }

        var user = await dbContext.AdminUsers
            .AsNoTracking()
            .Where(candidate => candidate.Id == administratorId)
            .Select(candidate => new
            {
                candidate.IsActive,
                candidate.SecurityStamp,
            })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null || !user.IsActive || user.SecurityStamp != securityStamp)
        {
            context.RejectPrincipal();
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(
        RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
