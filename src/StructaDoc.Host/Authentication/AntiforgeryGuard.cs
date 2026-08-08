using Microsoft.AspNetCore.Antiforgery;

namespace StructaDoc.Host.Authentication;

internal static class AntiforgeryGuard
{
    /// <summary>
    /// Returns the failure result to send back, or <see langword="null"/> when the request carries a
    /// valid token.
    /// </summary>
    internal static async Task<IResult?> ValidateAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Antiforgery validation failed",
                detail: "A valid antiforgery token is required.");
        }
    }
}
