using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Host.Auditing;

namespace StructaDoc.Host.Authentication;

public static class AdministratorAccountEndpoints
{
    public static IEndpointRouteBuilder MapAdministratorAccountEndpoints(
        this IEndpointRouteBuilder endpoints,
        TimeSpan sessionLifetime)
    {
        var group = endpoints.MapGroup("/api/v1/admin/administrators")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .RequireServiceMantleSecurityResponseHeaders();

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
            StructaDocManagementAuditRecorder auditRecorder,
            ILogger<ManagementAuditRecordPoints> logger,
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
                await RecordAccountAuditAsync(
                    auditRecorder,
                    logger,
                    user,
                    StructaDocManagementAudit.Actions.AdministratorPasswordChanged,
                    CurrentAdministratorId(user).ToString("D"),
                    user.FindFirstValue(StructaDocClaimTypes.Username)!,
                    ManagementAuditOutcome.Failure,
                    "The current password did not match.",
                    timeProvider,
                    HostManagementAudit.ClientAddress(context),
                    cancellationToken);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Current password is incorrect",
                    detail: "The current password does not match this account.");
            }

            if (result.Status != AdministratorAccountStatus.Succeeded)
            {
                await RecordAccountAuditAsync(
                    auditRecorder,
                    logger,
                    user,
                    StructaDocManagementAudit.Actions.AdministratorPasswordChanged,
                    CurrentAdministratorId(user).ToString("D"),
                    user.FindFirstValue(StructaDocClaimTypes.Username)!,
                    ManagementAuditOutcome.Failure,
                    $"The password change was rejected with status '{result.Status}'.",
                    timeProvider,
                    HostManagementAudit.ClientAddress(context),
                    cancellationToken);
                return Problem(result.Status);
            }

            await RecordAccountAuditAsync(
                auditRecorder,
                logger,
                user,
                StructaDocManagementAudit.Actions.AdministratorPasswordChanged,
                result.Administrator!.Id.ToString("D"),
                result.Administrator.Username,
                ManagementAuditOutcome.Success,
                "An administrator changed their own password.",
                timeProvider,
                HostManagementAudit.ClientAddress(context),
                cancellationToken);

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

    /// <summary>
    /// Account changes are the one record point family whose members share a shape: the acting
    /// administrator, the affected account, the action, and the outcome. The affected username is
    /// the only metadata, because it is the thing an administrator reading the trail needs to map
    /// an account id to a person.
    /// </summary>
    private static async Task RecordAccountAuditAsync(
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger logger,
        ClaimsPrincipal user,
        ManagementAuditAction action,
        string affectedAdministratorId,
        string affectedUsername,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        TimeProvider timeProvider,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var auditEvent = ManagementAuditEvent.Create(
            HostManagementAudit.AdministratorOperator(user),
            action,
            ManagementAuditTarget.Create(
                StructaDocManagementAudit.TargetTypes.AdministratorAccount,
                affectedAdministratorId),
            outcome,
            clientIp: clientIp,
            securityDescription: securityDescription,
            metadata: new Dictionary<string, string>
            {
                ["username"] = affectedUsername,
            },
            timeProvider: timeProvider);

        await HostManagementAudit.RecordAsync(
            auditRecorder,
            logger,
            auditEvent,
            "administrator account change",
            cancellationToken);
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
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorAccountService accounts,
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
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
            await RecordAccountAuditAsync(
                auditRecorder,
                logger,
                user,
                StructaDocManagementAudit.Actions.AdministratorAccountCreated,
                CurrentAdministratorId(user).ToString("D"),
                request.Username,
                ManagementAuditOutcome.Failure,
                $"The account creation was rejected with status '{result.Status}'.",
                timeProvider,
                HostManagementAudit.ClientAddress(context),
                cancellationToken);
            return Problem(result.Status);
        }

        var account = result.Account!;
        await RecordAccountAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.AdministratorAccountCreated,
            account.Id.ToString("D"),
            account.Username,
            ManagementAuditOutcome.Success,
            "An administrator account was created.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);

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
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
        TimeProvider timeProvider,
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
        await RecordAccountAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.AdministratorPasswordReset,
            id.ToString("D"),
            id.ToString("D"),
            status == AdministratorAccountStatus.Succeeded
                ? ManagementAuditOutcome.Success
                : ManagementAuditOutcome.Failure,
            status == AdministratorAccountStatus.Succeeded
                ? "Another administrator's password was reset."
                : $"The password reset was rejected with status '{status}'.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);
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
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
        TimeProvider timeProvider,
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
        await RecordAccountAuditAsync(
            auditRecorder,
            logger,
            user,
            request.IsActive
                ? StructaDocManagementAudit.Actions.AdministratorEnabled
                : StructaDocManagementAudit.Actions.AdministratorDisabled,
            id.ToString("D"),
            id.ToString("D"),
            status == AdministratorAccountStatus.Succeeded
                ? ManagementAuditOutcome.Success
                : ManagementAuditOutcome.Failure,
            status == AdministratorAccountStatus.Succeeded
                ? (request.IsActive
                    ? "An administrator account was enabled."
                    : "An administrator account was disabled.")
                : $"The account state change was rejected with status '{status}'.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);
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
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
        TimeProvider timeProvider,
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
        await RecordAccountAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.AdministratorDeleted,
            id.ToString("D"),
            id.ToString("D"),
            status == AdministratorAccountStatus.Succeeded
                ? ManagementAuditOutcome.Success
                : ManagementAuditOutcome.Failure,
            status == AdministratorAccountStatus.Succeeded
                ? "An administrator account was deleted."
                : $"The account deletion was rejected with status '{status}'.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);
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
