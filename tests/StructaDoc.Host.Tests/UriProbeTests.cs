namespace StructaDoc.Host.Tests;

public sealed class UriProbeTests
{
    [Fact]
    public void Probe_relative_uri_combination()
    {
        var baseAddress = new Uri("http://localhost/");
        var relative = new Uri("/api/v1/admin/session/login?returnUrl=%2Fadmin%2Fproviders", UriKind.RelativeOrAbsolute);
        var combined = new Uri(baseAddress, relative);
        var byString = new Uri(baseAddress, "/api/v1/admin/session/login?returnUrl=%2Fadmin%2Fproviders");
        Assert.Fail(
            $"combined.PathAndQuery={combined.PathAndQuery}; byString.PathAndQuery={byString.PathAndQuery}; "
            + $"combined.Query={combined.Query}; relative.OriginalString={relative.OriginalString}");
    }
}
