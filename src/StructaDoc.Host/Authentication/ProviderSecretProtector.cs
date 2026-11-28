using Microsoft.AspNetCore.DataProtection;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Authentication;

public sealed class ProviderSecretProtector : IProviderSecretProtector
{
    private readonly IDataProtector protector;

    public ProviderSecretProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("StructaDoc.ProviderCredentials.v1");
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}

public sealed class ProviderSubmissionProtector : IProviderSubmissionProtector
{
    private readonly IDataProtector protector;

    public ProviderSubmissionProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("StructaDoc.ProviderSubmissionContinuations.v1");
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}
