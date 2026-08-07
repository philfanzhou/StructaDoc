using StructaDoc.Application.Authentication;

namespace StructaDoc.Persistence.Tests;

public sealed class ExternalIdentityConstraintsTests
{
    [Theory]
    [InlineData("https://identity.example.com", true)]
    [InlineData("http://identity.test/tenant", true)]
    [InlineData("https://identity.example.com?tenant=one", false)]
    [InlineData("https://identity.example.com/#fragment", false)]
    [InlineData("identity.example.com", false)]
    [InlineData("https://身份.example.com", false)]
    public void Issuer_validation_matches_the_portable_oidc_storage_contract(
        string issuer,
        bool expected)
    {
        Assert.Equal(expected, ExternalIdentityConstraints.IsValidIssuer(issuer));
    }

    [Fact]
    public void Subject_is_ascii_case_sensitive_and_limited_to_255_characters()
    {
        Assert.True(ExternalIdentityConstraints.IsValidSubject("CaseSensitiveSubject"));
        Assert.True(ExternalIdentityConstraints.IsValidSubject(new string('a', 255)));
        Assert.False(ExternalIdentityConstraints.IsValidSubject(new string('a', 256)));
        Assert.False(ExternalIdentityConstraints.IsValidSubject(" subject "));
        Assert.False(ExternalIdentityConstraints.IsValidSubject("用户"));
    }
}
