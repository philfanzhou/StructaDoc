namespace StructaDoc.Application.Settings;

/// <summary>
/// Encrypts settings whose value must not be readable from the database file alone.
///
/// The control-plane database sits in the same directory as the rest of a deployment's data and is
/// routinely copied around as a backup. A client secret written there in the clear would travel with
/// every copy, so it is encrypted with the same key ring that already protects Provider credentials.
/// Losing that key ring makes the secret unreadable rather than corrupting the deployment: it is
/// reported as set, and an administrator can write a new one over it.
/// </summary>
public interface ISettingSecretProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// Returns <see langword="null"/> when the value cannot be decrypted, which happens when the key
    /// ring was lost or replaced. Callers report the setting as set but unreadable rather than
    /// failing, because an unreadable secret must still be replaceable from the web interface.
    /// </summary>
    string? TryUnprotect(string protectedValue);
}
