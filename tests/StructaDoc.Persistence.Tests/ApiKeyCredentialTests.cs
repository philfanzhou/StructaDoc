using System.Security.Cryptography;
using StructaDoc.Adapters.Authentication;

namespace StructaDoc.Persistence.Tests;

public sealed class ApiKeyCredentialTests
{
    [Fact]
    public void Issued_credential_round_trips_to_client_and_secret_hash()
    {
        var clientId = Guid.NewGuid();

        var issued = ApiKeyCredential.Create(clientId);
        var parsed = ApiKeyCredential.TryParse(
            issued.Credential,
            out var parsedClientId,
            out var parsedHash);

        Assert.True(parsed);
        Assert.Equal(clientId, parsedClientId);
        Assert.True(CryptographicOperations.FixedTimeEquals(issued.SecretHash, parsedHash));
        Assert.DoesNotContain(
            Convert.ToHexString(issued.SecretHash),
            issued.Credential,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sd1.not-a-guid.secret")]
    [InlineData("sd2.00000000000000000000000000000000.secret")]
    [InlineData("sd1.00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sd1.00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("sd1.00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public void Invalid_credential_is_rejected(string credential)
    {
        Assert.False(ApiKeyCredential.TryParse(credential, out _, out _));
    }

    [Fact]
    public void Credential_cannot_be_created_for_empty_client_id()
    {
        Assert.Throws<ArgumentException>(() => ApiKeyCredential.Create(Guid.Empty));
    }
}
