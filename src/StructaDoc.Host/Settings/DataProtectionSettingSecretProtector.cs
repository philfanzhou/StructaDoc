using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Settings;

public sealed class DataProtectionSettingSecretProtector : ISettingSecretProtector
{
    private readonly IDataProtector protector;

    public DataProtectionSettingSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("StructaDoc.Settings.v1");
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string? TryUnprotect(string protectedValue)
    {
        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // Raised when the key ring no longer holds the key this value was encrypted with, which
            // is a deployment that lost or replaced /data/keys rather than a programming error. The
            // caller reports the setting as set but unreadable so it can be written again.
            return null;
        }
    }
}
