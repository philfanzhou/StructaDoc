using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Authentication;

public static class AdministratorAccountEndpoints
{
    public static IEndpointRouteBuilder MapAdministratorAccountEndpoints(
        this IEndpointRouteBuilder endpoints,
        TimeSpan sessionLifetime)
    {
        var group = endpoints.MapGroup("/api/v1/admin/administrators")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AdministratorAccountResponse>>();
        group.MapPost("", CreateAsync)
            .Produces<AdministratorAccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/me/password", ChangeOwnPasswordAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPost("/{id:guid}/password", ResetPasswordAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{id:guid}/active", SetActiveAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;

        async Task<IResult> ChangeOwnPasswordAsync(
            ChangeOwnPasswordRequest request,
            ClaimsPrincipal user,
            HttpContext context,
            IAntiforgery antiforgery,
            IAdministratorAccountService accounts,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            var result = await accounts.ChangeOwnPasswordAsync(
                CurrentAdministratorId(user),
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);

            if (result.Status == AdministratorAccountStatus.IncorrectPassword)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Current password is incorrect",
                    detail: "The current password does not match this account.");
            }

            if (result.Status != AdministratorAccountStatus.Succeeded)
            {
                return Problem(result.Status);
            }

            // The change rotated the security stamp, which invalidates every cookie holding the old
            // one. The caller signed in correctly, so it is re-issued rather than signed out; other
            // sessions of the same account stay invalidated.
            await context.SignInAsync(
                AuthenticationSchemes.AdministratorCookie,
                AdministratorSessionEndpoints.CreatePrincipal(result.Administrator!),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = timeProvider.GetUtcNow().Add(sessionLifetime),
                });

            return Results.NoContent();
        }
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        IAdministratorAccountService accounts,
        CancellationToken cancellationToken)
    {
        var currentId = CurrentAdministratorId(user);
        var administrators = await accounts.ListAsync(cancellationToken);
        return Results.Ok(administrators
            .Select(account => new AdministratorAccountResponse(
                account.Id,
                account.Username,
                account.DisplayName,
                account.IsActive,
                account.CreatedAtUtc,
                account.LastLoginAtUtc,
                account.Id == currentId))
            .ToArray());
    }

    private static async Task<IResult> CreateAsync(
        CreateAdministratorRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorAccountService accounts,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await accounts.CreateAsync(
            request.Username,
            request.Password,
            request.DisplayName,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        if (result.Status != AdministratorAccountStatus.Succeeded)
        {
            return Problem(result.Status);
        }

        var account = result.Account!;
        return Results.Created(
            $"/api/v1/admin/administrators/{account.Id:D}",
            new AdministratorAccountResponse(
                account.Id,
                account.Username,
                account.DisplayName,
                account.IsActive,
                account.CreatedAtUtc,
                account.LastLoginAtUtc,
                IsCurrent: false));
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        ResetAdministratorPasswordRequest request,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorAccountService accounts,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        // Resetting another account needs no current password, so allowing it against the caller's
        // own account would leave that requirement with nothing to enforce.
        if (id == CurrentAdministratorId(user))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Own password cannot be reset here",
                detail: "Change your own password through /api/v1/admin/administrators/me/password, which requires the current one.");
        }

        var status = await accounts.ResetPasswordAsync(id, request.NewPassword, cancellationToken);
        return status == AdministratorAccountStatus.Succeeded
            ? Results.NoContent()
            : Problem(status, id);
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id,
        SetAdministratorActiveRequest request,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorAccountService accounts,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        if (!request.IsActive && id == CurrentAdministratorId(user))
        {
            return SelfRemovalProblem("disable");
        }

        var status = await accounts.SetActiveAsync(id, request.IsActive, cancellationToken);
        return status == AdministratorAccountStatus.Succeeded
            ? Results.NoContent()
            : Problem(status, id);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorAccountService accounts,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        if (id == CurrentAdministratorId(user))
        {
            return SelfRemovalProblem("delete");
        }

        var status = await accounts.DeleteAsync(id, cancellationToken);
        return status == AdministratorAccountStatus.Succeeded
            ? Results.NoContent()
            : Problem(status, id);
    }

    private static Guid CurrentAdministratorId(ClaimsPrincipal user)
    {
        return Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    private static IResult SelfRemovalProblem(string action)
    {
        // Self-removal is the one lockout an administrator can cause without another administrator
        // noticing, and it is never the only way to reach the intended outcome.
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Own account cannot be changed this way",
            detail: $"An administrator cannot {action} their own account. Ask another administrator to do it.");
    }

    private static IResult Problem(AdministratorAccountStatus status, Guid? id = null)
    {
        return status switch
        {
            AdministratorAccountStatus.InvalidUsername => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid username",
                detail: $"A username contains {AdministratorUsernamePolicy.MinimumLength} to {AdministratorUsernamePolicy.MaximumLength} letters, digits, '.', '_', or '-', and starts and ends with a letter or digit."),

            AdministratorAccountStatus.InvalidPassword => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid password",
                detail: $"A password contains {AdministratorPasswordPolicy.MinimumLength} to {AdministratorPasswordPolicy.MaximumLength} characters."),

            AdministratorAccountStatus.UsernameInUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Username is in use",
                detail: "Another administrator already uses this username."),

            AdministratorAccountStatus.LastActiveAdministrator => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Last active administrator",
                detail: "This is the only active administrator. Add or enable another one first, otherwise nothing could administer this deployment."),

            _ => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Administrator not found",
                detail: id is null
                    ? "The administrator does not exist."
                    : $"Administrator '{id:D}' does not exist."),
        };
    }
}
