using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StructaDoc.Application.Authentication;
using StructaDoc.Platform.Authentication;
using StructaDoc.Platform.Persistence;

namespace StructaDoc.Host.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    StructaDocDbContext dbContext)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string HeaderPrefix = "ApiKey ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();

        if (!authorization.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var credential = authorization[HeaderPrefix.Length..].Trim();

        if (!ApiKeyCredential.TryParse(credential, out var clientId, out var suppliedHash))
        {
            return AuthenticateResult.Fail("Invalid API credential.");
        }

        var client = await dbContext.ApiClients
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == clientId, Context.RequestAborted);

        if (client is null
            || !client.IsActive
            || client.RevokedAtUtc is not null
            || client.SecretHash.Length != suppliedHash.Length
            || !CryptographicOperations.FixedTimeEquals(client.SecretHash, suppliedHash))
        {
            return AuthenticateResult.Fail("Invalid API credential.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.Id.ToString("D")),
            new(ClaimTypes.Name, client.Name),
            new(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient),
        };
        claims.AddRange(client.Scopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(scope => new Claim(StructaDocClaimTypes.Scope, scope)));

        var identity = new ClaimsIdentity(claims, AuthenticationSchemes.ApiKey);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, AuthenticationSchemes.ApiKey));
    }
}
