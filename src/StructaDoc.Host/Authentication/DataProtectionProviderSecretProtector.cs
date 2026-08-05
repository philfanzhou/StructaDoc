using Microsoft.AspNetCore.DataProtection;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Authentication;

public sealed class DataProtectionProviderSecretProtector : IProviderSecretProtector
{
    private readonly IDataProtector protector;

    public DataProtectionProviderSecretProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("StructaDoc.ProviderCredentials.v1");
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
}
