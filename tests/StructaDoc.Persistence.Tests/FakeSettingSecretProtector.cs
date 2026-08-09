using StructaDoc.Application.Settings;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// Marks a value with a known prefix instead of encrypting it. What is under test is the rule for
/// reading a stored secret and for what happens when it cannot be read; using Data Protection here
/// would test the framework instead, and would make an unreadable value awkward to arrange.
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
