namespace StructaDoc.Application.Providers;

public interface IProviderSubmissionProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
