using StructaDoc.Application.Authentication;

namespace StructaDoc.Persistence.Tests;

public sealed class ApiClientDefinitionTests
{
    [Fact]
    public void Definition_normalizes_name_and_scopes()
    {
        var succeeded = ApiClientDefinition.TryCreate(
            "  Integration  ",
            [
                AuthenticationScopes.ParsesWrite,
                AuthenticationScopes.DocumentsRead,
                AuthenticationScopes.ParsesWrite,
            ],
            out var definition,
            out _,
            out _);

        Assert.True(succeeded);
        Assert.NotNull(definition);
        Assert.Equal("Integration", definition.Name);
        Assert.Equal(
            [AuthenticationScopes.DocumentsRead, AuthenticationScopes.ParsesWrite],
            definition.Scopes);
    }

    [Fact]
    public void Empty_scope_collection_is_valid()
    {
        Assert.True(ApiClientDefinition.TryCreate(
            "No permissions",
            [],
            out var definition,
            out _,
            out _));
        Assert.Empty(definition!.Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DOCUMENTS:READ")]
    [InlineData("unknown:scope")]
    public void Invalid_scope_is_rejected(string? scope)
    {
        Assert.False(ApiClientDefinition.TryCreate(
            "Integration",
            [scope],
            out _,
            out var errorField,
            out _));
        Assert.Equal("scopes", errorField);
    }
}
