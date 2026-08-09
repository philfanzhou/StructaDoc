using Microsoft.AspNetCore.DataProtection;
using StructaDoc.Platform.Authentication;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// The Data Protection key ring, in the one form that can be used before the application is built.
///
/// Stored settings are read into configuration before dependency injection exists, and one of them is
/// encrypted, so the key ring has to be available earlier than the container that normally provides
/// it. The same instance is then registered for the rest of the application, which keeps a single
/// key ring rather than two readers of the same directory that could disagree about it.
/// </summary>
public static class StructaDocKeyRing
{
    /// <summary>
    /// Fixes the purpose chain across processes. A different name would produce keys that cannot
    /// decrypt anything written before, so it is set in one place rather than repeated.
    /// </summary>
    public const string ApplicationName = "StructaDoc";

    public static IDataProtectionProvider Create(StructaDocAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keyPath = Path.GetFullPath(options.DataProtectionKeysPath);
        Directory.CreateDirectory(keyPath);

        return DataProtectionProvider.Create(
            new DirectoryInfo(keyPath),
            builder => builder.SetApplicationName(ApplicationName));
    }
}
