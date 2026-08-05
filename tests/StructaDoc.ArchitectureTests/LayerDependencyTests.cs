using System.Reflection;

namespace StructaDoc.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_reference_other_product_layers()
    {
        AssertReferencesOnly(StructaDoc.Domain.AssemblyReference.Assembly);
    }

    [Fact]
    public void Contracts_do_not_reference_other_product_layers()
    {
        AssertReferencesOnly(typeof(StructaDoc.Contracts.System.ServiceInfoResponse).Assembly);
    }

    [Fact]
    public void Application_only_references_domain()
    {
        AssertReferencesOnly(
            Application.AssemblyReference.Assembly,
            "StructaDoc.Domain");
    }

    [Fact]
    public void Infrastructure_only_references_inner_layers()
    {
        AssertReferencesOnly(
            Infrastructure.AssemblyReference.Assembly,
            "StructaDoc.Application",
            "StructaDoc.Domain");
    }

    private static void AssertReferencesOnly(Assembly assembly, params string[] allowedReferences)
    {
        var actualReferences = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("StructaDoc.", StringComparison.Ordinal))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unexpectedReferences = actualReferences
            .Except(allowedReferences, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedReferences);
    }
}
