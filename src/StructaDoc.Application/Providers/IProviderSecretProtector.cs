namespace StructaDoc.Application.Providers;

public interface IProviderSecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
