using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StructaDoc.Adapters.Authentication;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// The shared browser contract of the two OIDC sign-in entries: the workspace entry
/// (<c>GET /api/v1/session/login</c>) and the gated administration entry
/// (<c>GET /api/v1/admin/session/login</c>). The protocol itself runs on the framework's own
/// OpenID Connect handler with no provider-specific package, so a standards-compliant provider —
/// SignaCore, Keycloak, Authentik, Entra ID — is a configuration matter, not a code matter.
/// </summary>
/// <remarks>
/// The administration entry arms a pre-sign-in gate: a sign-in that started there may establish a
/// session only for a subject whose verified ID token carries the configured administrator role.
/// Failing the gate prevents the sign-in entirely — no ticket and no cookie are written — and the
/// browser lands on one bounded outcome, the same fixed set every other protocol failure maps to.
/// No failure detail beyond the bounded code ever reaches the browser.
/// </remarks>
public static class OidcSignInFlow
{
    /// <summary>
    /// The challenge <see cref="AuthenticationProperties.Items"/> key that marks a sign-in as
    /// having started at the administration entry. The value travels inside the handler's
    /// data-protected <c>state</c>, so it survives the redirect round trip and cannot be forged.
    /// </summary>
    internal const string AdminEntryItem = "structadoc.oidc.entry";

    /// <summary>The <see cref="AdminEntryItem"/> value marking the administration entry.</summary>
    internal const string AdminEntryValue = "admin";

    /// <summary>
    /// The failure marker of the pre-sign-in administrator gate. Matched by message at the remote
    /// failure event, because a failed token-validation event carries no properties.
    /// </summary>
    internal const string AdminGateFailure = "The signed-in subject does not carry the administrator role.";

    /// <summary>The bounded outcome values of the <c>authentication</c> landing query parameter.</summary>
    internal const string DeniedOutcome = "denied";
    internal const string NotAdministratorOutcome = "not-admin";
    internal const string UnavailableOutcome = "unavailable";
    internal const string FailedOutcome = "failed";

    /// <summary>
    /// Starts the authorization-code challenge. An unreachable authority — discovery, JWKS — is
    /// answered with the fixed unavailable landing rather than an error page, because the
    /// sign-in entry is where an operator lands when the identity provider is down.
    /// </summary>
    internal static Task BeginSignInAsync(
        HttpContext context,
        OidcAuthenticationOptions options,
        string? returnUrl,
        bool administratorEntry,
        string defaultReturnUrl)
    {
        if (!options.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "OIDC authentication is disabled").ExecuteAsync(context);
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = NormalizeReturnUrl(returnUrl, defaultReturnUrl),
        };
        if (administratorEntry)
        {
            properties.Items[AdminEntryItem] = AdminEntryValue;
        }

        return ChallengeAsync(context, properties);
    }

    /// <summary>
    /// The fixed landing of one completed or failed sign-in, composed from the entry the flow
    /// started at and one bounded outcome code.
    /// </summary>
    internal static string LandingPath(bool administratorEntry, string outcome)
        => administratorEntry
            ? $"/admin/signin?authentication={outcome}"
            : $"/signin?authentication={outcome}";

    /// <summary>
    /// Maps a remote-authentication failure to the bounded outcome and the entry it lands on. The
    /// gate denial implies the administration entry on its own, because the gate only arms there;
    /// every other failure reads the entry from the round-tripped challenge properties, which are
    /// absent when the protected state could not be decoded at all.
    /// </summary>
    internal static string FailureLanding(Exception? failure, AuthenticationProperties? properties)
    {
        var administratorEntry = IsAdministratorEntry(properties);

        if (failure?.Message == AdminGateFailure)
        {
            return LandingPath(administratorEntry: true, NotAdministratorOutcome);
        }

        if (failure is OpenIdConnectProtocolException protocol
            && string.Equals(protocol.Data["error"] as string, "access_denied", StringComparison.Ordinal))
        {
            return LandingPath(administratorEntry, DeniedOutcome);
        }

        return LandingPath(
            administratorEntry,
            failure is not null && IsAuthorityUnavailable(failure) ? UnavailableOutcome : FailedOutcome);
    }

    /// <summary>
    /// Whether the challenge properties mark the administration entry. The item travels inside the
    /// handler's protected state and is absent from a workspace-entry sign-in, which is itself the
    /// answer.
    /// </summary>
    internal static bool IsAdministratorEntry(AuthenticationProperties? properties)
        => properties?.Items.TryGetValue(AdminEntryItem, out var value) == true
            && string.Equals(value, AdminEntryValue, StringComparison.Ordinal);

    /// <summary>
    /// Whether the failure chain is a transport failure of the authority's discovery, JWKS, or
    /// token endpoints. IdentityModel wraps document-retrieval errors, so every link of the chain
    /// is inspected.
    /// </summary>
    internal static bool IsAuthorityUnavailable(Exception failure)
    {
        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is HttpRequestException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ChallengeAsync(HttpContext context, AuthenticationProperties properties)
    {
        try
        {
            await context.ChallengeAsync(AuthenticationSchemes.OpenIdConnect, properties);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            context.Response.Redirect(LandingPath(
                IsAdministratorEntry(properties),
                UnavailableOutcome));
        }
    }

    private static string NormalizeReturnUrl(string? returnUrl, string fallback)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
        {
            return fallback;
        }

        return returnUrl;
    }
}
