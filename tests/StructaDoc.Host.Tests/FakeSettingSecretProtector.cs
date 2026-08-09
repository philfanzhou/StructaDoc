using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Marks a value with a known prefix instead of encrypting it, for the tests that replace the
/// settings configuration and have nothing to do with secrets.
/// </summary>
internal sealed class FakeSettingSecretProtector : ISettingSecretProtector
{
    public const string Prefix = "protected:";

    public string Protect(string plaintext) => Prefix + plaintext;

    public string? TryUnprotect(string protectedValue)
    {
        return protectedValue.StartsWith(Prefix, StringComparison.Ordinal)
            ? protectedValue[Prefix.Length..]
            : null;
    }
}
