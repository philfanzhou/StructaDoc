namespace StructaDoc.Adapters.Authentication;

/// <summary>
/// The Authentication keys that only tune local administration sign-in: how long an administrator
/// session lasts and how tightly the two sign-in endpoints are rate limited. Nothing here locates
/// the key ring or unlocks stored secrets, so unlike <see cref="StructaDocAuthenticationOptions"/>
/// these keys are bound from the settings-aware configuration — the one an administrator's stored
/// values have already joined — which is what makes them settable from the browser. They take
/// effect at the next start, because the cookie and both rate-limit policies are built once at
/// composition.
/// </summary>
public sealed class AuthenticationTuningOptions
{
    public const string SectionName = "Authentication";

    public TimeSpan AdministratorSessionLifetime { get; init; } = TimeSpan.FromHours(8);

    public int LoginPermitLimit { get; init; } = 10;

    public TimeSpan LoginRateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (AdministratorSessionLifetime < TimeSpan.FromMinutes(5)
            || AdministratorSessionLifetime > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException(
                "Authentication:AdministratorSessionLifetime must be between 5 minutes and 7 days.");
        }

        if (LoginPermitLimit is < 1 or > 1000)
        {
            throw new InvalidOperationException(
                "Authentication:LoginPermitLimit must be between 1 and 1000.");
        }

        if (LoginRateLimitWindow < TimeSpan.FromSeconds(1)
            || LoginRateLimitWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Authentication:LoginRateLimitWindow must be between 1 second and 1 hour.");
        }
    }
}
