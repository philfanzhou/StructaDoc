using System.Text.Json;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Settings;

/// <summary>
/// Outcome codes are stable tokens rather than sentences so the web interface can say what happened
/// in its own language. <paramref name="Detail"/> carries the part only the deployment knows, such as
/// a status code or the issuer that came back.
/// </summary>
public sealed record OidcDiscoveryResult(string Code, string Detail = "", string? Issuer = null)
{
    public bool Succeeded => Code == OidcDiscoveryCodes.Reachable;
}

public static class OidcDiscoveryCodes
{
    public const string Reachable = "Reachable";
    public const string InvalidAuthority = "InvalidAuthority";
    public const string InsecureAuthority = "InsecureAuthority";
    public const string Unreachable = "Unreachable";
    public const string TimedOut = "TimedOut";
    public const string HttpError = "HttpError";
    public const string MalformedDocument = "MalformedDocument";
    public const string IncompleteDocument = "IncompleteDocument";
    public const string IssuerMismatch = "IssuerMismatch";
}

/// <summary>
/// Fetches an identity provider's discovery document so an administrator learns that an authority is
/// wrong while saving it, rather than from a user who cannot sign in.
///
/// This does not reach private addresses through the guard that Provider transfers use, and that is
/// deliberate: a self-hosted deployment's identity provider is very often on the same private network
/// as the service, so refusing those addresses would reject the common case. The address is supplied
/// by an authenticated administrator, who already configures outbound Provider endpoints.
///
/// Only the authority is checked. Whether the client id and secret are accepted cannot be learned
/// without completing a sign-in, so this never claims they were.
/// </summary>
public sealed class OidcDiscoveryProbe : IDisposable
{
    private const int MaximumDocumentBytes = 256 * 1024;

    private readonly HttpClient client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<OidcDiscoveryResult> ProbeAsync(
        string? authority,
        bool requireHttpsMetadata,
        CancellationToken cancellationToken = default)
    {
        // Accepted through the same rule that decides what can be saved, so a value this reports as
        // reachable is never one the settings endpoint would then refuse.
        var definition = SettingCatalog.Find(SettingCatalog.OidcAuthority)!;
        var normalized = SettingCatalog.Normalize(definition, authority);
        if (normalized is null)
        {
            return new OidcDiscoveryResult(OidcDiscoveryCodes.InvalidAuthority);
        }

        var address = new Uri(normalized, UriKind.Absolute);
        if (requireHttpsMetadata && address.Scheme != Uri.UriSchemeHttps)
        {
            return new OidcDiscoveryResult(OidcDiscoveryCodes.InsecureAuthority);
        }

        var document = new Uri(normalized + "/.well-known/openid-configuration");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                document,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OidcDiscoveryResult(OidcDiscoveryCodes.TimedOut);
        }
        catch (HttpRequestException error)
        {
            return new OidcDiscoveryResult(OidcDiscoveryCodes.Unreachable, error.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new OidcDiscoveryResult(
                    OidcDiscoveryCodes.HttpError,
                    ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            string issuer;
            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();

                // Bounded because the address is administrator-supplied and nothing guarantees the
                // thing answering it is an identity provider at all.
                await CopyBoundedAsync(content, buffer, cancellationToken);
                buffer.Position = 0;

                using var parsed = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
                var root = parsed.RootElement;

                if (!TryReadString(root, "issuer", out issuer)
                    || !TryReadString(root, "authorization_endpoint", out _)
                    || !TryReadString(root, "token_endpoint", out _)
                    || !TryReadString(root, "jwks_uri", out _))
                {
                    return new OidcDiscoveryResult(OidcDiscoveryCodes.IncompleteDocument);
                }
            }
            catch (JsonException)
            {
                return new OidcDiscoveryResult(OidcDiscoveryCodes.MalformedDocument);
            }
            catch (InvalidOperationException)
            {
                return new OidcDiscoveryResult(OidcDiscoveryCodes.MalformedDocument);
            }
            catch (HttpRequestException error)
            {
                return new OidcDiscoveryResult(OidcDiscoveryCodes.Unreachable, error.Message);
            }

            // The sign-in middleware rejects a token whose issuer is not the authority it was
            // configured with, so an authority that does not match its own document would fail only
            // once a user tried to sign in. Trailing slashes are ignored: the two spellings address
            // the same provider.
            return string.Equals(
                issuer.TrimEnd('/'),
                normalized,
                StringComparison.OrdinalIgnoreCase)
                ? new OidcDiscoveryResult(OidcDiscoveryCodes.Reachable, Issuer: issuer)
                : new OidcDiscoveryResult(OidcDiscoveryCodes.IssuerMismatch, Issuer: issuer);
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > MaximumDocumentBytes)
            {
                throw new InvalidOperationException(
                    "The discovery document exceeded the size a discovery document may have.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public void Dispose() => client.Dispose();
}
