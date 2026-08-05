namespace StructaDoc.Application.Providers;

public sealed class ProviderCredential
{
    private readonly string value;

    public ProviderCredential(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        this.value = value;
    }

    public string Reveal() => value;

    public override string ToString() => "[redacted]";
}
